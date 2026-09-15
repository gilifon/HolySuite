"""
THE LETTER NETWORK: hears the decoder's readings, names each letter in the gap after it.

HOW IT GOT HERE, in three steps, each measured:

1. The element network (MorseAngel's design, retrained on this band) answered "which element of the
   character is this?" and the letter was built by counting. 93.6% right on clean CW, 0 of 34 on the
   air: one miscounted element turns an R into an F.

2. CTC removes the counting - the network just says which letter - and learns alignment by itself.
   It could not learn it here. On perfectly clean square-wave CW, and even asked only to memorise
   sixteen examples of one or two letters, it said "A" for every input and never climbed out,
   whatever the learning rate, cell type, input scale or number of steps (CtcProbe, CtcDebug). The
   loss and decoding were proven correct with hand-built answers (CtcChain). CTC's weakness is known:
   until a letter happens to land in the right place it gets almost no clue which letter.

3. But the generator knows exactly when every letter was sent. So every moment is labelled: nothing,
   except the letter itself in the gap straight after it. Dense teaching like the element network's,
   naming whole letters like CTC's, nothing to count. On the same tiny task it read 100 of 100
   examples it had never seen after 1500 steps (FramewiseProbe). This file is that, at full size.

THE SHAPE: one 1-D convolution over the five readings (20 ms either side), two LSTM layers of 128,
one linear layer, at the full 10 ms rate - labels are per frame, so nothing is thrown away.

TRAINED ON CROPS: eight-second windows cut anywhere, which labels make possible (CTC could not crop:
it has no idea which letters fall inside a window). Long overs cost nothing and short ones waste
nothing.

DECODED like CTC: likeliest answer each moment, repeats merged, "nothing" dropped.

THE ONLY SCORE THAT DECIDES ANYTHING is the real-signal bench printed at every check. The plain
decoder scores 32. Kept: the checkpoint best on held-back GENERATED audio, never the one best on the
recordings - choosing by 34 words would fit the network to them.
"""

import os
import struct
import sys
import time

import numpy as np
import torch
import torch.nn as nn

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import ALPHABET, BLANK, CLASSES, BENCH, MUST_STAY_QUIET, greedy, edit_distance, flatten  # noqa: E402

CROP = 800


def read(path):
    with open(path, "rb") as f:
        raw = f.read()
    assert raw[:8] == b"CWFEAT02", "needs a CWFEAT02 file from the current DumpFeatures"
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
        (n,) = struct.unpack_from("<i", raw, at); at += 4
        labels = np.frombuffer(raw, dtype=np.uint8, count=n, offset=at).astype(np.int64).copy()
        at += n
        out.append((name, text, data, labels))
    return out, bins


class LetterNet(nn.Module):
    def __init__(self, bins):
        super().__init__()
        self.conv = nn.Conv1d(bins, 64, 5, padding=2)
        self.lstm = nn.LSTM(64, 128, 2, batch_first=True)
        self.linear = nn.Linear(128, CLASSES)

    def forward(self, x):
        h = torch.relu(self.conv(x.transpose(1, 2))).transpose(1, 2)
        h, _ = self.lstm(h)
        return self.linear(h)


def look(examples, many=2):
    """Labels against the note's own feature, so the alignment can be seen, not assumed."""
    shown = 0
    for name, text, d, lab in examples:
        if not text or len(lab) == 0 or len(text) > 20:
            continue
        centre = d[:, d.shape[1] // 2]
        if np.percentile(centre, 90) < 1.5:
            continue
        print("%s  \"%s\"" % (name, text))
        span = slice(0, 260)
        print("  audio  " + "".join(" .:-=+*#%@"[max(0, min(9, int((v + 0.5) * 3)))] for v in centre[span]))
        print("  label  " + "".join("." if c == BLANK else ("_" if c == 1 else ALPHABET[c - 1]) for c in lab[span]))
        shown += 1
        if shown >= many:
            return


def real_score(model, real):
    found = wanted = noise = 0
    lines = []
    model.eval()
    with torch.no_grad():
        for name, _, d, _ in real:
            said = greedy(model(torch.from_numpy(d).unsqueeze(0))[0])
            flat = flatten(said)
            if name in MUST_STAY_QUIET:
                noise += len(flat) // 20
                lines.append("    %-9s %5d letters of noise" % (name, len(flat)))
            elif name in BENCH:
                hits = [w for w in BENCH[name] if w in flat]
                found += len(hits)
                wanted += len(BENCH[name])
                lines.append("    %-9s %d of %d   %s" % (name, len(hits), len(BENCH[name]), " ".join(said.split())[:80]))
    model.train()
    return found - noise, found, wanted, noise, lines


def save(model, path):
    with open(path, "wb") as f:
        f.write(b"CWLETTR1")
        items = list(model.state_dict().items())
        f.write(struct.pack("<i", len(items)))
        for name, t in items:
            raw = name.encode("ascii")
            f.write(struct.pack("<i", len(raw))); f.write(raw)
            f.write(struct.pack("<i", t.dim()))
            for d in t.shape:
                f.write(struct.pack("<i", d))
            f.write(t.detach().cpu().numpy().astype("<f4").tobytes())


def main(training_file, real_file, out, minutes):
    torch.manual_seed(4)
    rng = np.random.RandomState(4)

    examples, bins = read(training_file)
    real, _ = read(real_file)
    # A recording of nothing but the band has no letters, so it has no marks and DumpFeatures writes
    # no labels for it. It is NOT to be thrown away: every frame of it is "nothing", and it is the
    # only thing that teaches the network what an empty frequency sounds like. The first run filtered
    # these out by accident - 1,547 of 8,000 examples, every silence lesson in the set.
    kept = []
    for name, text, d, lab in examples:
        if len(d) <= 60:
            continue
        if len(lab) == 0 and not text.strip():
            lab = np.zeros(len(d), dtype=np.int64)
        if len(lab) == len(d):
            kept.append((name, text, d, lab))
    examples = kept
    print("%d examples, %d of them nothing but the band" % (len(examples), sum(1 for e in examples if not e[1].strip())))
    look(examples)

    rng.shuffle(examples)
    held = max(100, len(examples) // 10)
    heldout, training = examples[:held], examples[held:]
    print("\n%d training, %d held back, %d real recordings" % (len(training), len(heldout), len(real)))

    model = LetterNet(bins)
    weights = torch.ones(CLASSES)
    weights[BLANK] = 0.1
    loss_of = nn.CrossEntropyLoss(weight=weights, ignore_index=-100)
    opt = torch.optim.Adam(model.parameters(), lr=2e-3)

    started, step, best = time.time(), 0, 9.9
    while time.time() - started < minutes * 60:
        x = np.zeros((32, CROP, bins), dtype=np.float32)
        y = np.full((32, CROP), -100, dtype=np.int64)
        for k in range(32):
            _, _, d, lab = training[rng.randint(len(training))]
            if len(d) > CROP:
                s = rng.randint(0, len(d) - CROP)
                x[k] = d[s:s + CROP]
                y[k] = lab[s:s + CROP]
            else:
                x[k, :len(d)] = d
                y[k, :len(d)] = lab

        logits = model(torch.from_numpy(x))
        loss = loss_of(logits.reshape(-1, CLASSES), torch.from_numpy(y).reshape(-1))
        opt.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        opt.step()
        step += 1

        if step % 200 == 0:
            model.eval()
            errors = chars = 0
            with torch.no_grad():
                for _, text, d, _ in heldout[:150]:
                    said = " ".join(greedy(model(torch.from_numpy(d).unsqueeze(0))[0]).split())
                    truth = " ".join(text.upper().split())
                    errors += edit_distance(said, truth)
                    chars += max(1, len(truth))
            model.train()
            cer = errors / chars
            score, found, wanted, noise, _ = real_score(model, real)
            mark = ""
            if cer < best:
                best = cer
                save(model, out)
                mark = "  <- saved"
            print("step %5d  %4.0f min  loss %.3f  held-back char error %5.1f%%  REAL %2d of %d words, -%d noise => %d%s"
                  % (step, (time.time() - started) / 60, loss.item(), cer * 100, found, wanted, noise, score, mark))
            sys.stdout.flush()

    save(model, out + ".last")
    score, found, wanted, noise, lines = real_score(model, real)
    print("\nlast network on the real recordings:")
    print("\n".join(lines))
    print("REAL %d of %d words, -%d noise => %d   (plain decoder: 32)" % (found, wanted, noise, score))


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "--look":
        look(read(sys.argv[2])[0], many=4)
    else:
        main(sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4]) if len(sys.argv) > 4 else 45)
