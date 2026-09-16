"""
WHAT DOES THE REAL BAND LOOK LIKE THAT THE PRACTICE AUDIO DOES NOT?

The letter network stopped improving on the real recordings while it went on improving on practice
audio, so the difference between the two is now the limit. This measures that difference instead of
guessing it, on the five numbers the network actually hears.

For each recording it reports, for the note's own feature:
  on     - how loud the key-down stretches are (the top fifth of frames)
  off    - how loud the key-up stretches are (the bottom fifth)
  depth  - on minus off: how clearly the keying stands out. This is what the network reads.
  side   - how loud the neighbours are, relative to the note, when the key is down: a clean station
           has quiet neighbours; a strong one spills into them, and AGC pumping lifts them too.
  flicker- how often the note's feature crosses its own middle - chopped or fluttering keying.
and it prints a strip of the note's feature so the shape can be seen.
"""

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainLetters import read  # noqa: E402


def describe(name, d, strip=True):
    centre = d[:, 2]
    active = centre[centre > np.percentile(centre, 30)]
    if len(active) < 20:
        return
    on = np.percentile(centre, 90)
    off = np.percentile(centre, 15)
    down = centre > (on + off) / 2
    side = (d[down, 0] + d[down, 1] + d[down, 3] + d[down, 4]).mean() / 4 - centre[down].mean() if down.any() else 0
    crossings = np.count_nonzero(np.diff(down.astype(int)) != 0) / (len(centre) / 100.0)
    print("  %-12s on %5.2f  off %5.2f  depth %5.2f  side %+5.2f  flicker %5.1f/s" % (name, on, off, on - off, side, crossings))
    if strip:
        busiest = int(np.argmax(np.convolve(down.astype(float), np.ones(300), "same")))
        s = max(0, busiest - 120)
        print("               " + "".join(" .:-=+*#%@"[max(0, min(9, int((v + 0.5) * 3)))] for v in centre[s:s + 240]))


def main(real_file, practice_file):
    real, _ = read(real_file)
    print("REAL RECORDINGS")
    for name, _, d, _ in real:
        describe(name, d)

    practice, _ = read(practice_file)
    strong = [(n, t, d) for n, t, d, _ in practice if t.strip() and np.percentile(d[:, 2], 90) > 1.5][:6]
    weak = [(n, t, d) for n, t, d, _ in practice if t.strip() and np.percentile(d[:, 2], 90) < 1.0][:3]
    print("\nPRACTICE - strong")
    for name, _, d in strong:
        describe(name, d)
    print("\nPRACTICE - weak")
    for name, _, d in weak:
        describe(name, d)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
