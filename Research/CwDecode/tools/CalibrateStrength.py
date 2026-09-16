"""
SET THE SIGNAL STRENGTH FROM THE REAL RECORDINGS.

The AGC did not bring practice audio into line with the real band; plain strength may. For each
candidate range (a power of ten of the full keying amplitude), makes a small batch, extracts the
features, and reports the depth of EVERY signal example - the whole spread, not only the strong -
beside the real recordings'. The range whose spread sits over the real 0.9 to 1.5 is the one.
"""

import os
import subprocess
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from CalibrateAgc import depth, real_depths, show, ROOT, BUILD, TRAIN  # noqa: E402
from TrainLetters import read  # noqa: E402


def batch(low, high, count=120, seed=903):
    folder = os.path.join(TRAIN, "cal")
    subprocess.run([os.path.join(BUILD, "MakeCtcAudio.exe"), os.path.join(ROOT, "recordings"), folder,
                    str(count), str(seed), "0.3", "0", "0", "0", str(low), str(high)], check=True, capture_output=True)
    out = os.path.join(TRAIN, "cal.bin")
    subprocess.run([os.path.join(BUILD, "DumpFeatures.exe"), out, os.path.join(folder, "index.txt"), folder],
                   check=True, capture_output=True)
    examples, _ = read(out)
    return [depth(d) for n, t, d, _ in examples if t.strip()]


def main():
    show("REAL RECORDINGS", real_depths())
    for low, high in [(-0.85, 0.0), (-1.8, -1.0), (-1.7, -0.8), (-1.9, -0.9)]:
        show("practice, strength %.2f..%.2f" % (low, high), batch(low, high))


if __name__ == "__main__":
    main()
