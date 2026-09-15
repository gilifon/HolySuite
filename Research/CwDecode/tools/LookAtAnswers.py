"""
WHAT IS THE LETTER NETWORK ACTUALLY SAYING, MOMENT BY MOMENT?

Its loss falls fast but its character error stays above 100%, which means more letters are coming out
than went in. Prints, for a few held-back examples, the audio, the label and the network's answer in
three rows, so the extra letters can be seen: flicker inside one label, or letters where there is none.
"""

import os
import struct
import sys

import numpy as np
import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import ALPHABET, BLANK, greedy  # noqa: E402
from TrainLetters import LetterNet, read  # noqa: E402


def load(model, path):
    with open(path, "rb") as f:
        raw = f.read()
    assert raw[:8] == b"CWLETTR1"
    at = 8
    (count,) = struct.unpack_from("<i", raw, at); at += 4
    state = {}
    for _ in range(count):
        (n,) = struct.unpack_from("<i", raw, at); at += 4
        name = raw[at:at + n].decode(); at += n
        (dims,) = struct.unpack_from("<i", raw, at); at += 4
        shape = []
        for _ in range(dims):
            (d,) = struct.unpack_from("<i", raw, at); at += 4
            shape.append(d)
        total = int(np.prod(shape)) if shape else 1
        state[name] = torch.from_numpy(np.frombuffer(raw, dtype="<f4", count=total, offset=at).reshape(shape).copy())
        at += total * 4
    model.load_state_dict(state)


def row(classes):
    return "".join("." if c == BLANK else ("_" if c == 1 else ALPHABET[c - 1]) for c in classes)


def main(features, weights, many=4):
    examples, bins = read(features)
    model = LetterNet(bins)
    load(model, weights)
    model.eval()

    shown = 0
    for name, text, d, lab in examples[-400:]:
        if not text.strip() or len(lab) != len(d):
            continue
        centre = d[:, bins // 2]
        if np.percentile(centre, 90) < 1.5:
            continue
        with torch.no_grad():
            said = model(torch.from_numpy(d).unsqueeze(0))[0].argmax(-1).numpy()
        first = int(np.argmax(lab != BLANK)) - 60
        span = slice(max(0, first), max(0, first) + 240)
        print("%s  sent \"%s\"   read \"%s\"" % (name, text, " ".join(greedy(model(torch.from_numpy(d).unsqueeze(0))[0]).split())))
        print("  audio  " + "".join(" .:-=+*#%@"[max(0, min(9, int((v + 0.5) * 3)))] for v in centre[span]))
        print("  label  " + row(lab[span]))
        print("  said   " + row(said[span]))
        print()
        shown += 1
        if shown >= many:
            break


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
