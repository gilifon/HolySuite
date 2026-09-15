"""
TEACHES THE NETWORK TO READ THIS OPERATOR'S BAND.

Same shape as the one it replaces - one number in, two LSTM layers of sixty, seven answers out -
because the C# that runs it has been checked number for number against an independent implementation
and is not worth risking. Only the weights change, and they are written back into the same
MORSENN1 file the program already loads.

WHAT IS DIFFERENT IS WHAT IT IS TAUGHT FROM. The network being replaced was trained on a clean tone
plus computer hiss at around thirteen words a minute. This is trained on CW keyed into eleven
minutes of real band noise recorded off this operator's own receiver - static crashes, other
stations and all - across twelve to forty words a minute, four hundred to eight hundred Hz, and
signal strengths over two orders of magnitude, with fading.

THE ANSWERS, and the convention was read off the original network rather than guessed (LabelProbe):
    0  the character has ended        1  the word has ended
    2..6  the first .. fifth element of this character is in progress, INCLUDING the gap after it.

Chopped into chunks and the state carried between them, so a long over teaches the network as much
as a short one and nothing has to be padded.
"""

import struct
import sys
import time

import numpy as np
import torch
import torch.nn as nn


CHUNK = 320          # steps per training window - about four characters
WARMUP = 80          # ...of which the first stretch is read but NOT scored - see below
HIDDEN = 60
LAYERS = 2
OUTPUTS = 7


class MorseNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.lstm = nn.LSTM(1, HIDDEN, LAYERS, batch_first=True)
        self.linear = nn.Linear(HIDDEN, OUTPUTS)

    def forward(self, x, state=None):
        out, state = self.lstm(x, state)
        return self.linear(out), state


def load(path, limit=None):
    """Every example as (envelope, labels), both float32/int64 arrays of the same length."""
    examples = []
    for line in open(path, encoding="utf-8"):
        bits = line.rstrip("\n").split("\t")
        if len(bits) < 4:
            continue
        env = np.fromstring(bits[2], sep=" ", dtype=np.float32)
        lab = np.fromstring(bits[3], sep=" ", dtype=np.int64)
        if len(env) != len(lab) or len(env) < CHUNK:
            continue
        examples.append((env, lab))
        if limit and len(examples) >= limit:
            break
    return examples


def chunks(examples, batch):
    """
    Windows of CHUNK steps, drawn at random across every example.

    Not whole overs: a whole over is thousands of steps and backpropagating through all of it at
    once is slow and unstable. Four characters is enough for the network to learn where it is
    inside a character, which is the only thing it is being asked.

    AND THE FIRST WARMUP STEPS ARE NOT SCORED. This was the fault that stalled the first run flat
    at 57%. A window opens at a random moment with the network's memory blank, and if it opens in
    the middle of a character then NOTHING in the audio says whether the element in progress is the
    first or the third - that is in the history the network has not been given. Punishing it for
    failing to know an unknowable thing taught it to hedge, and the accuracy stopped climbing.
    Now it reads the opening of the window to find its place, and is scored only after it has had
    the chance to see a character boundary. In service the state is carried and this never arises.
    """
    while True:
        xs, ys = [], []
        for _ in range(batch):
            env, lab = examples[np.random.randint(len(examples))]
            at = np.random.randint(0, len(env) - CHUNK)
            xs.append(env[at:at + CHUNK])
            ys.append(lab[at:at + CHUNK])
        yield (torch.from_numpy(np.stack(xs)).unsqueeze(-1),
               torch.from_numpy(np.stack(ys)))


def save_weights(model, path):
    """The MORSENN1 file the C# side reads - see CwNeuralNet.Load and morse/extract.py."""
    named = [
        ("lstm.weight_ih_l0", model.lstm.weight_ih_l0),
        ("lstm.weight_hh_l0", model.lstm.weight_hh_l0),
        ("lstm.bias_ih_l0", model.lstm.bias_ih_l0),
        ("lstm.bias_hh_l0", model.lstm.bias_hh_l0),
        ("lstm.weight_ih_l1", model.lstm.weight_ih_l1),
        ("lstm.weight_hh_l1", model.lstm.weight_hh_l1),
        ("lstm.bias_ih_l1", model.lstm.bias_ih_l1),
        ("lstm.bias_hh_l1", model.lstm.bias_hh_l1),
        ("linear.weight", model.linear.weight),
        ("linear.bias", model.linear.bias),
    ]

    with open(path, "wb") as f:
        f.write(b"MORSENN1")
        f.write(struct.pack("<i", len(named)))
        for name, tensor in named:
            raw = name.encode("ascii")
            f.write(struct.pack("<i", len(raw)))
            f.write(raw)
            shape = list(tensor.shape)
            f.write(struct.pack("<i", len(shape)))
            for d in shape:
                f.write(struct.pack("<i", d))
            values = tensor.detach().cpu().numpy().astype("<f4").ravel()
            f.write(values.tobytes())


def main(labelled, out, minutes=20.0, batch=64):
    torch.manual_seed(1)
    np.random.seed(1)

    examples = load(labelled)
    if not examples:
        print("no examples in " + labelled)
        return
    steps_total = sum(len(e) for e, _ in examples)
    print("%d examples, %d steps of audio (%.0f seconds of CW at 7.69 steps a dit)"
          % (len(examples), steps_total, steps_total / 7.69 * 0.06))

    # A quarter held back and never trained on, so "it is learning" can be told from "it is
    # memorising". Split by EXAMPLE, not by window: windows from one over share a noise bed and
    # a speed, and splitting those across the two halves would make the test set look easy.
    cut = max(1, len(examples) // 4)
    holdout, training = examples[:cut], examples[cut:]
    print("%d for training, %d held back" % (len(training), len(holdout)))

    model = MorseNet()
    loss_of = nn.CrossEntropyLoss()
    optimiser = torch.optim.Adam(model.parameters(), lr=3e-3)

    feed = chunks(training, batch)

    # THE SAME TEST WINDOWS EVERY TIME. Drawing a fresh random batch to score against made the
    # reading wobble by three points between one check and the next, which is more than the whole
    # run improves by - so "best so far" latched onto a lucky early reading and later, better
    # networks were never saved. Fixed windows, drawn once: the number still has noise in it, but
    # it is the SAME noise each time, so two readings can be compared.
    np.random.seed(99)
    fixed_x, fixed_y = next(chunks(holdout, 512))
    np.random.seed(1)

    started = time.time()
    step = 0
    best = 0.0

    while time.time() - started < minutes * 60:
        x, y = next(feed)
        out_, _ = model(x)
        loss = loss_of(out_[:, WARMUP:, :].reshape(-1, OUTPUTS), y[:, WARMUP:].reshape(-1))

        optimiser.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        optimiser.step()
        step += 1

        if step % 200 == 0:
            model.eval()
            with torch.no_grad():
                pv, _ = model(fixed_x)
                right = (pv.argmax(-1)[:, WARMUP:] == fixed_y[:, WARMUP:]).float().mean().item()
            model.train()

            print("  %5d steps  %5.0fs  loss %.4f  held-back accuracy %.1f%%%s"
                  % (step, time.time() - started, loss.item(), right * 100,
                     "  <- best, saved" if right > best else ""))
            sys.stdout.flush()

            if right > best:
                best = right
                save_weights(model, out)

    # The last state as well as the best, because the two are usually within a point of each other
    # and the last one has seen the most.
    save_weights(model, out.replace(".bin", "-last.bin"))
    print("done: best held-back accuracy %.1f%%, weights in %s (and the final state beside it)"
          % (best * 100, out))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2],
         minutes=float(sys.argv[3]) if len(sys.argv) > 3 else 20.0)
