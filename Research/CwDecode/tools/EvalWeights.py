"""
HOW WELL DOES A GIVEN SET OF WEIGHTS ANSWER MY LABELS?

Run with the ORIGINAL author's weights this answers a question worth far more than any training
run: is my labelling convention his? His network is known to work - it decodes clean Morse - so if
it scores badly against my labels then the labels are wrong, and no amount of training will help.
If it scores well, the labels are right and a low score from my own network is simply underfitting.

Scored two ways, because the plain average is misleading. Silence is most of the audio, so a
network that answered "word ended" and nothing else would look respectable; the per-answer figures
below show whether it can actually find the elements.
"""

import struct
import sys

import numpy as np
import torch
import torch.nn as nn

HIDDEN, LAYERS, OUTPUTS = 60, 2, 7
NAMES = ["char ended", "word ended", "element 1", "element 2", "element 3", "element 4", "element 5"]


class MorseNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.lstm = nn.LSTM(1, HIDDEN, LAYERS, batch_first=True)
        self.linear = nn.Linear(HIDDEN, OUTPUTS)

    def forward(self, x, state=None):
        out, state = self.lstm(x, state)
        return self.linear(out), state


def load_weights(model, path):
    with open(path, "rb") as f:
        raw = f.read()
    assert raw[:8] == b"MORSENN1", "not a MORSENN1 file"
    at = 8
    (count,) = struct.unpack_from("<i", raw, at); at += 4

    found = {}
    for _ in range(count):
        (n,) = struct.unpack_from("<i", raw, at); at += 4
        name = raw[at:at + n].decode(); at += n
        (dims,) = struct.unpack_from("<i", raw, at); at += 4
        shape = []
        for _ in range(dims):
            (d,) = struct.unpack_from("<i", raw, at); at += 4
            shape.append(d)
        total = int(np.prod(shape))
        found[name] = np.frombuffer(raw, dtype="<f4", count=total, offset=at).reshape(shape).copy()
        at += total * 4

    with torch.no_grad():
        for name, tensor in [("lstm.weight_ih_l0", model.lstm.weight_ih_l0),
                             ("lstm.weight_hh_l0", model.lstm.weight_hh_l0),
                             ("lstm.bias_ih_l0", model.lstm.bias_ih_l0),
                             ("lstm.bias_hh_l0", model.lstm.bias_hh_l0),
                             ("lstm.weight_ih_l1", model.lstm.weight_ih_l1),
                             ("lstm.weight_hh_l1", model.lstm.weight_hh_l1),
                             ("lstm.bias_ih_l1", model.lstm.bias_ih_l1),
                             ("lstm.bias_hh_l1", model.lstm.bias_hh_l1),
                             ("linear.weight", model.linear.weight),
                             ("linear.bias", model.linear.bias)]:
            tensor.copy_(torch.from_numpy(found[name]))


def main(weights, labelled, only_strong=True, limit=300):
    model = MorseNet()
    load_weights(model, weights)
    model.eval()

    confusion = np.zeros((OUTPUTS, OUTPUTS), dtype=np.int64)
    n = 0

    for line in open(labelled, encoding="utf-8"):
        bits = line.rstrip("\n").split("\t")
        if len(bits) < 4:
            continue
        env = np.fromstring(bits[2], sep=" ", dtype=np.float32)
        lab = np.fromstring(bits[3], sep=" ", dtype=np.int64)
        if len(env) != len(lab):
            continue

        # The clean ones only, by default. A network cannot be blamed for missing an element that
        # is not audible, and the question here is whether the LABELS are the right shape.
        if only_strong and env.max() < 0.95:
            continue

        with torch.no_grad():
            out, _ = model(torch.from_numpy(env).view(1, -1, 1))
        said = out.argmax(-1).view(-1).numpy()

        for t, p in zip(lab, said):
            confusion[t, p] += 1

        n += 1
        if n >= limit:
            break

    total = confusion.sum()
    if total == 0:
        print("nothing to score")
        return

    right = np.trace(confusion)
    print("%s on %d strong examples" % (weights, n))
    print("  overall %.1f%%" % (100.0 * right / total))
    print("  per answer (how often the right answer was given when it WAS the right answer):")
    for i in range(OUTPUTS):
        row = confusion[i].sum()
        if row:
            print("    %-11s %5.1f%%   (%d steps)" % (NAMES[i], 100.0 * confusion[i, i] / row, row))
    print("  what it says instead, for the element answers:")
    for i in range(2, OUTPUTS):
        row = confusion[i].sum()
        if row:
            worst = int(np.argmax([confusion[i, j] if j != i else -1 for j in range(OUTPUTS)]))
            print("    %-11s most often mistaken for %s (%.0f%%)"
                  % (NAMES[i], NAMES[worst], 100.0 * confusion[i, worst] / row))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
