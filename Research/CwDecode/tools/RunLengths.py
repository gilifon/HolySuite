"""
HOW LONG DOES EACH NETWORK HOLD AN ANSWER?

This is the number the C# side actually decides on. It does not look at the network's answer moment
by moment - it counts how many steps in a row one element answer is held, and calls anything past
its boundary a dah. So two networks can be equally accurate step by step and still be read
completely differently, if one holds its answers longer than the other.

The boundary in CwNeuralDecoder is 21 steps, which was measured off the ORIGINAL network: it holds
each answer about six steps past the end of the mark, so a dit reads about 14 and a dah about 30.
A replacement network that holds for a different length needs a different boundary, or every
element lands on the wrong side of the line and the text is rubbish however good the network is.
"""

import struct
import sys

import numpy as np
import torch
import torch.nn as nn

PER_DIT = 7.69
HIDDEN, LAYERS, OUTPUTS = 60, 2, 7

MORSE = {'A': ".-", 'C': "-.-.", 'D': "-..", 'E': ".", 'G': "--.", 'I': "..", 'K': "-.-",
         'L': ".-..", 'M': "--", 'N': "-.", 'O': "---", 'Q': "--.-", 'R': ".-.", 'S': "...",
         'T': "-", 'U': "..-", 'W': ".--", 'Z': "--..", '4': "....-", '5': ".....", '7': "--...",
         '3': "...--", '9': "----.", 'P': ".--.", 'B': "-...", 'F': "..-.", 'H': "....",
         'V': "...-", 'X': "-..-", 'Y': "-.--", 'J': ".---"}


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
        for name, tensor in [("lstm.weight_ih_l0", model.lstm.weight_ih_l0), ("lstm.weight_hh_l0", model.lstm.weight_hh_l0),
                             ("lstm.bias_ih_l0", model.lstm.bias_ih_l0), ("lstm.bias_hh_l0", model.lstm.bias_hh_l0),
                             ("lstm.weight_ih_l1", model.lstm.weight_ih_l1), ("lstm.weight_hh_l1", model.lstm.weight_hh_l1),
                             ("lstm.bias_ih_l1", model.lstm.bias_ih_l1), ("lstm.bias_hh_l1", model.lstm.bias_hh_l1),
                             ("linear.weight", model.linear.weight), ("linear.bias", model.linear.bias)]:
            tensor.copy_(torch.from_numpy(found[name]))


def build(text):
    """Clean square-wave CW at the front end's own clock, with which element is a dit or a dah."""
    env, truth = [], []

    def hold(dits, on):
        n = int(round(dits * PER_DIT))
        env.extend([1.0 if on else 0.0] * n)

    hold(4, False)
    for c in text.upper():
        if c == ' ':
            hold(4, False)
            continue
        pattern = MORSE.get(c)
        if not pattern:
            continue
        for i, e in enumerate(pattern):
            truth.append(e)
            hold(3 if e == '-' else 1, True)
            if i < len(pattern) - 1:
                hold(1, False)
        hold(3, False)
    hold(6, False)
    return np.array(env, dtype=np.float32), truth


def main(weights):
    model = MorseNet()
    load_weights(model, weights)
    model.eval()

    text = "CQ CQ DE 4Z5SL 4Z5SL K PARIS 599 73 THE QUICK BROWN FOX"
    env, truth = build(text)

    with torch.no_grad():
        out, _ = model(torch.from_numpy(env).view(1, -1, 1))
    said = out.argmax(-1).view(-1).numpy()

    # Every unbroken run of an element answer, in order.
    runs = []
    at = 0
    while at < len(said):
        here = said[at]
        start = at
        while at < len(said) and said[at] == here:
            at += 1
        if here >= 2:
            runs.append(at - start)

    dits = [r for r, e in zip(runs, truth) if e == '.']
    dahs = [r for r, e in zip(runs, truth) if e == '-']

    print("%s" % weights)
    print("  element runs found: %d, elements actually sent: %d" % (len(runs), len(truth)))
    if dits:
        print("  a dit is held %.1f steps on average (%d to %d)" % (np.mean(dits), min(dits), max(dits)))
    if dahs:
        print("  a dah is held %.1f steps on average (%d to %d)" % (np.mean(dahs), min(dahs), max(dahs)))
    if dits and dahs:
        line = (np.mean(dits) * np.mean(dahs)) ** 0.5
        print("  the line between them belongs at %.0f steps  (the C# side uses 21)" % line)


if __name__ == "__main__":
    main(sys.argv[1])
