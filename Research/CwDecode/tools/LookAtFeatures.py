"""
Do the features actually show the CW? Prints the note's own feature for a few strong, slowish
examples as a strip of characters, 10 ms each, so keying can be seen by eye. If there is no keying
in the strip, no amount of training will find any.
"""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import read_features  # noqa: E402


def main(features, index, many=3):
    examples, bins = read_features(features)
    meta = {}
    for line in open(index, encoding="utf-8"):
        b = line.rstrip("\n").split("\t")
        meta[b[0]] = b

    lengths = [len(d) for _, _, d in examples]
    print("frames per example: min %d  median %d  max %d"
          % (min(lengths), int(np.median(lengths)), max(lengths)))

    shown = 0
    for name, text, d in examples:
        b = meta.get(name)
        if not b or not text or float(b[3]) < 0.8 or float(b[2]) > 22:
            continue
        centre = d[:, bins // 2]
        print("\n%s  \"%s\"  %s WPM  strength %s" % (name, text, b[2], b[3]))
        print("  " + "".join(" .:-=+*#%@"[max(0, min(9, int((v + 0.5) * 3)))] for v in centre[:220]))
        print("  spread of the note's feature: 10%% %.2f   50%% %.2f   90%% %.2f"
              % tuple(np.percentile(centre, [10, 50, 90])))
        shown += 1
        if shown >= many:
            break


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
