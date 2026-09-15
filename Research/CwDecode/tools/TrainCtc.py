"""
THE CTC NETWORK: hears the decoder's readings, answers with letters.

WHY CTC, and why the network before this one failed. That network was asked, every moment, "which
element of the character is this - first, second, third?" and the letter was assembled by counting.
It learned the timing well - 93.6% right on clean CW - and still spelled nothing on the air, because
one miscounted element turns a three-element letter into a five-element one. Counting is brittle in
exactly the conditions that matter.

CTC takes the counting away. The network answers "which letter, or nothing" every 20 ms, and the
loss works out for itself where each letter of the known text must have been. Decoding is just:
take the likeliest answer each moment, merge repeats, drop the "nothing"s. There is nothing to
miscount.

WHAT IT HEARS (see DumpFeatures.cs, where the recipe lives and must not drift): every 10 ms, the
loudness at the note and two frequencies either side, in logs above the noise floor. No speed is
assumed - the readings come at a fixed rate and the network learns speed itself.

THE SHAPE, kept plain so the C# port is plain: two 1-D convolutions (the second halves the rate to
20 ms), two LSTM layers of 128, one linear layer. Nothing in it is exotic.

THE ONLY SCORE THAT DECIDES ANYTHING is the real-signal bench, printed at every check: the same
recordings and the same expected words RealBench.cs uses, minus a mark for every 20 letters printed
on the two recordings that must stay quiet. The plain decoder scores 32. The checkpoint kept is the
one best on held-back GENERATED audio, not on these recordings - choosing by the recordings would
quietly fit the network to 34 words and flatter it.
"""

import struct
import sys
import time

import numpy as np
import torch
import torch.nn as nn

ALPHABET = " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/=?.,"
BLANK = 0                      # class 0 is "nothing"; class i+1 is ALPHABET[i]
CLASSES = len(ALPHABET) + 1
CHAR_TO_CLASS = {c: i + 1 for i, c in enumerate(ALPHABET)}

# Must match RealBench.cs.
BENCH = {
    "quiet":   ["V4TQ", "5NN", "IZ5CMG", "SP9ADG"],
    "radio":   ["PWR", "SUR", "DELTA", "22MUP", "MERCI", "POURCE", "AUPLAISIR", "SOIR", "BONNE"],
    "radio2":  ["RIGRIG", "80W"],
    "narrow":  ["LB2WD", "DERA1QN", "73GL"],
    "bad":     ["LB2WD", "DERA1Q"],
    "ly2px2":  ["K1Y", "TOMEET", "AGN"],
    "ly2px":   ["LY2PX", "CQCQCQDE"],
    "slow":    ["IU5RDL", "CQCQ", "PSE"],
    "weak":    ["GUD", "HIHI", "AGN"],
    "active":  ["R1LN", "DER1LN", "7388"],
}
MUST_STAY_QUIET = ["beacons", "session1"]


def read_features(path):
    """Every example in a DumpFeatures file as (name, text, float32 array frames x bins)."""
    with open(path, "rb") as f:
        raw = f.read()
    assert raw[:8] == b"CWFEAT01", "not a DumpFeatures file"
    at = 8
    count, bins = struct.unpack_from("<ii", raw, at); at += 8
    out = []
    for _ in range(count):
        (n,) = struct.unpack_from("<i", raw, at); at += 4
        name = raw[at:at + n].decode("utf-8"); at += n
        (n,) = struct.unpack_from("<i", raw, at); at += 4
        text = raw[at:at + n].decode("utf-8"); at += n
        (frames,) = struct.unpack_from("<i", raw, at); at += 4
        data = np.frombuffer(raw, dtype="<f4", count=frames * bins, offset=at).reshape(frames, bins).copy()
        at += frames * bins * 4
        out.append((name, text, data))
    return out, bins


def encode(text):
    """Text as class numbers - runs of spaces collapsed, anything outside the alphabet dropped."""
    cleaned = " ".join(text.upper().split())
    return [CHAR_TO_CLASS[c] for c in cleaned if c in CHAR_TO_CLASS]


class CtcNet(nn.Module):
    """
    40 ms A STEP, NOT 20. The first shape halved the 10 ms frames once and could barely learn: on
    perfectly clean square-wave CW it was still saying nothing after five hundred steps and only
    single letters after a thousand. Too many time steps leave CTC a very long way to search for
    where each letter sits. Halving twice still leaves a letter several steps even at 40 WPM, where
    the shortest one, E and its gap, lasts 120 ms.
    """

    def __init__(self, bins):
        super().__init__()
        self.conv1 = nn.Conv1d(bins, 48, 5, padding=2)
        self.conv2 = nn.Conv1d(48, 64, 5, stride=2, padding=2)
        self.conv3 = nn.Conv1d(64, 96, 5, stride=2, padding=2)
        self.lstm = nn.LSTM(96, 128, 2, batch_first=True)
        self.linear = nn.Linear(128, CLASSES)

    def forward(self, x):                      # x: batch, frames, bins
        h = x.transpose(1, 2)
        h = torch.relu(self.conv1(h))
        h = torch.relu(self.conv2(h))
        h = torch.relu(self.conv3(h))
        h = h.transpose(1, 2)
        h, _ = self.lstm(h)
        return self.linear(h)                  # batch, frames/4, classes

    @staticmethod
    def out_length(frames):
        half = (frames + 2 * 2 - 5) // 2 + 1
        return (half + 2 * 2 - 5) // 2 + 1


def greedy(logits):
    """Likeliest answer each moment, repeats merged, "nothing" dropped."""
    best = logits.argmax(-1).tolist()
    text, last = [], BLANK
    for c in best:
        if c != last and c != BLANK:
            text.append(ALPHABET[c - 1])
        last = c
    return "".join(text)


def edit_distance(a, b):
    row = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        prev, row[0] = row[0], i
        for j in range(1, len(b) + 1):
            cur = row[j]
            row[j] = min(row[j] + 1, row[j - 1] + 1, prev + (a[i - 1] != b[j - 1]))
            prev = cur
    return row[len(b)]


def flatten(s):
    return "".join(c for c in s.upper() if c.isalnum())


def real_score(model, real):
    """The bench: words found on the real recordings, minus noise printed where there is none."""
    found = wanted = noise = 0
    lines = []
    model.eval()
    with torch.no_grad():
        for name, _, data in real:
            said = greedy(model(torch.from_numpy(data).unsqueeze(0))[0])
            flat = flatten(said)
            if name in MUST_STAY_QUIET:
                noise += len(flat) // 20
                lines.append("    %-9s %5d letters of noise" % (name, len(flat)))
            elif name in BENCH:
                hits = [w for w in BENCH[name] if w in flat]
                found += len(hits)
                wanted += len(BENCH[name])
                lines.append("    %-9s %d of %d   %s" % (name, len(hits), len(BENCH[name]), said[:70]))
    model.train()
    return found - noise, found, wanted, noise, lines


def batches(examples, size, rng):
    """Examples of similar length together, so little of each batch is padding."""
    order = sorted(range(len(examples)), key=lambda i: len(examples[i][2]))
    groups = [order[i:i + size] for i in range(0, len(order), size)]
    rng.shuffle(groups)
    return groups


def save(model, path):
    tensors = [(name, p) for name, p in model.state_dict().items()]
    with open(path, "wb") as f:
        f.write(b"MORSECTC")
        f.write(struct.pack("<i", len(tensors)))
        for name, t in tensors:
            raw = name.encode("ascii")
            f.write(struct.pack("<i", len(raw))); f.write(raw)
            f.write(struct.pack("<i", t.dim()))
            for d in t.shape:
                f.write(struct.pack("<i", d))
            f.write(t.detach().cpu().numpy().astype("<f4").tobytes())


def main(training_file, real_file, out, minutes):
    torch.manual_seed(3)
    rng = np.random.RandomState(3)

    examples, bins = read_features(training_file)
    real, _ = read_features(real_file)
    examples = [(n, t, d) for n, t, d in examples if len(d) > 40]
    rng.shuffle(examples)

    held = max(50, len(examples) // 10)
    heldout, training = examples[:held], examples[held:]
    print("%d training, %d held back, %d real recordings, %d features a frame"
          % (len(training), len(heldout), len(real), bins))

    model = CtcNet(bins)
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=True)
    opt = torch.optim.Adam(model.parameters(), lr=3e-3)

    started = time.time()
    step, best_cer = 0, 9.9
    epoch = 0

    while time.time() - started < minutes * 60:
        epoch += 1
        for group in batches(training, 20, rng):
            if time.time() - started >= minutes * 60:
                break
            items = [training[i] for i in group]
            longest = max(len(d) for _, _, d in items)
            x = np.zeros((len(items), longest, bins), dtype=np.float32)
            targets, target_lengths, input_lengths = [], [], []
            for k, (_, text, d) in enumerate(items):
                x[k, :len(d)] = d
                code = encode(text)
                targets.extend(code)
                target_lengths.append(len(code))
                input_lengths.append(CtcNet.out_length(len(d)))

            logits = model(torch.from_numpy(x))
            log_probs = logits.log_softmax(-1).transpose(0, 1)
            loss = ctc(log_probs,
                       torch.tensor(targets, dtype=torch.long),
                       torch.tensor(input_lengths, dtype=torch.long),
                       torch.tensor(target_lengths, dtype=torch.long))
            opt.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
            opt.step()
            step += 1

            if step % 150 == 0:
                model.eval()
                errors = chars = 0
                with torch.no_grad():
                    for _, text, d in heldout[:150]:
                        said = greedy(model(torch.from_numpy(d).unsqueeze(0))[0])
                        truth = " ".join(text.upper().split())
                        errors += edit_distance(said, truth)
                        chars += max(1, len(truth))
                model.train()
                cer = errors / chars
                score, found, wanted, noise, lines = real_score(model, real)
                mark = ""
                if cer < best_cer:
                    best_cer = cer
                    save(model, out)
                    mark = "  <- best on held-back, saved"
                print("step %5d  epoch %d  %4.0f min  loss %.3f  held-back char error %5.1f%%  REAL %d of %d words, -%d noise => %d%s"
                      % (step, epoch, (time.time() - started) / 60, loss.item(), cer * 100,
                         found, wanted, noise, score, mark))
                sys.stdout.flush()

    save(model, out.replace(".bin", "-last.bin"))
    score, found, wanted, noise, lines = real_score(model, real)
    print("\nfinal network on the real recordings:")
    print("\n".join(lines))
    print("REAL %d of %d words, -%d noise => %d   (plain decoder: 32)" % (found, wanted, noise, score))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4]) if len(sys.argv) > 4 else 40)
