"""
THE LABELLING CONVENTION, SETTLED ON PERFECTLY CLEAN INPUT.

No audio, no front end, no noise: the envelope is built straight as a square wave at the front
end's own clock - 7.69 numbers to a dit, three times that to a dah - which is the ideal the front
end is trying to produce. Whatever the original author's network answers to THAT is his convention
with nothing else mixed in.

Two candidate conventions are scored, each slid in time:
  AFTER  - element k's label covers element k and the gap that FOLLOWS it
  BEFORE - element k's label covers the gap that PRECEDES it and then element k

Everything else about this project's training depends on getting this right, so it is worth
settling on input where there is no argument about what is in it.
"""

import struct
import sys

import numpy as np
import torch
import torch.nn as nn

PER_DIT = 7.69
HIDDEN, LAYERS, OUTPUTS = 60, 2, 7

MORSE = {
    'A': ".-", 'B': "-...", 'C': "-.-.", 'D': "-..", 'E': ".", 'F': "..-.", 'G': "--.",
    'H': "....", 'I': "..", 'J': ".---", 'K': "-.-", 'L': ".-..", 'M': "--", 'N': "-.",
    'O': "---", 'P': ".--.", 'Q': "--.-", 'R': ".-.", 'S': "...", 'T': "-", 'U': "..-",
    'V': "...-", 'W': ".--", 'X': "-..-", 'Y': "-.--", 'Z': "--..",
    '0': "-----", '1': ".----", '2': "..---", '3': "...--", '4': "....-", '5': ".....",
    '6': "-....", '7': "--...", '8': "---..", '9': "----.",
}


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


def build(text, convention):
    env, lab = [], []

    def hold(dits, on, what):
        n = int(round(dits * PER_DIT))
        env.extend([1.0 if on else 0.0] * n)
        lab.extend([what] * n)

    hold(4, False, 1)
    for c in text.upper():
        if c == ' ':
            hold(4, False, 1)
            continue
        pattern = MORSE.get(c)
        if not pattern:
            continue
        for i, e in enumerate(pattern):
            here = 2 + i
            if convention == "BEFORE" and i > 0:
                hold(1, False, here)              # the gap belongs to the element about to come
            hold(3 if e == '-' else 1, True, here)
            if convention == "AFTER" and i < len(pattern) - 1:
                hold(1, False, here)              # the gap belongs to the element just finished
        hold(3, False, 0)
    hold(6, False, 1)

    return np.array(env, dtype=np.float32), np.array(lab, dtype=np.int64)


def main(weights):
    model = MorseNet()
    load_weights(model, weights)
    model.eval()

    text = "CQ CQ DE 4Z5SL 4Z5SL K PARIS THE QUICK BROWN FOX 599 73"

    for convention in ("AFTER", "BEFORE"):
        env, lab = build(text, convention)
        with torch.no_grad():
            out, _ = model(torch.from_numpy(env).view(1, -1, 1))
        said = out.argmax(-1).view(-1).numpy()

        best, at = 0.0, 0
        for shift in range(-16, 17):
            if shift >= 0:
                a, b = lab[:len(lab) - shift], said[shift:]
            else:
                a, b = lab[-shift:], said[:len(said) + shift]
            share = float((a == b).mean())
            if share > best:
                best, at = share, shift
        print("%-7s best %.1f%% at shift %+d" % (convention, best * 100, at))

    # And what it actually says, beside the better convention, so it can be read by eye.
    env, lab = build(text, "BEFORE")
    with torch.no_grad():
        out, _ = model(torch.from_numpy(env).view(1, -1, 1))
    said = out.argmax(-1).view(-1).numpy()
    print()
    print("first 150 steps, BEFORE convention:")
    print("  envelope " + "".join("#" if v > 0.5 else "." for v in env[:150]))
    print("  labels   " + "".join(str(v) for v in lab[:150]))
    print("  network  " + "".join(str(v) for v in said[:150]))


if __name__ == "__main__":
    main(sys.argv[1])
