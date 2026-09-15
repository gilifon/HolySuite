"""
WHERE DO MY LABELS ACTUALLY BELONG IN TIME?

The original author's network works - it decodes clean Morse - so whatever it answers IS the
convention. Scored against my labels it manages only 40%, and every element is read as the NEXT
one, which is the signature of a constant offset in time rather than of confusion.

So slide the labels against his answers and see where the agreement peaks. The peak is the truth,
and it is worth far more than any argument about what the gap inside a character "should" be
labelled: a wrong offset teaches a new network to answer early or late by a whole element, and no
amount of training corrects for being taught the wrong thing.

A positive shift means my labels are EARLY - the real answer comes later than I wrote it.
"""

import struct
import sys

import numpy as np
import torch
import torch.nn as nn

HIDDEN, LAYERS, OUTPUTS = 60, 2, 7


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
    assert raw[:8] == b"MORSENN1"
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
        pairs = [("lstm.weight_ih_l0", model.lstm.weight_ih_l0), ("lstm.weight_hh_l0", model.lstm.weight_hh_l0),
                 ("lstm.bias_ih_l0", model.lstm.bias_ih_l0), ("lstm.bias_hh_l0", model.lstm.bias_hh_l0),
                 ("lstm.weight_ih_l1", model.lstm.weight_ih_l1), ("lstm.weight_hh_l1", model.lstm.weight_hh_l1),
                 ("lstm.bias_ih_l1", model.lstm.bias_ih_l1), ("lstm.bias_hh_l1", model.lstm.bias_hh_l1),
                 ("linear.weight", model.linear.weight), ("linear.bias", model.linear.bias)]
        for name, tensor in pairs:
            tensor.copy_(torch.from_numpy(found[name]))


def main(weights, labelled, limit=120):
    model = MorseNet()
    load_weights(model, weights)
    model.eval()

    pairs = []
    for line in open(labelled, encoding="utf-8"):
        bits = line.rstrip("\n").split("\t")
        if len(bits) < 4:
            continue
        env = np.fromstring(bits[2], sep=" ", dtype=np.float32)
        lab = np.fromstring(bits[3], sep=" ", dtype=np.int64)
        if len(env) != len(lab) or env.max() < 0.95:
            continue
        with torch.no_grad():
            out, _ = model(torch.from_numpy(env).view(1, -1, 1))
        pairs.append((lab, out.argmax(-1).view(-1).numpy()))
        if len(pairs) >= limit:
            break

    print("sliding %d strong examples against the original network" % len(pairs))
    best, bestShift = 0.0, 0
    for shift in range(-24, 25, 2):
        right = total = 0
        for lab, said in pairs:
            if shift >= 0:
                a, b = lab[:len(lab) - shift], said[shift:]
            else:
                a, b = lab[-shift:], said[:len(said) + shift]
            right += int((a == b).sum())
            total += len(a)
        share = right / total
        star = ""
        if share > best:
            best, bestShift = share, shift
            star = "  <-"
        print("  shift %+3d steps   %.1f%%%s" % (shift, share * 100, star))

    print()
    print("best at %+d steps: %.1f%%" % (bestShift, best * 100))
    print("(a positive shift means my labels are EARLY - the real answer comes that much later)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
