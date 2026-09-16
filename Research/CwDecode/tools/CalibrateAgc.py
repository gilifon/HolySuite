"""
SET THE AGC FROM THE REAL RECORDINGS, NOT FROM A GUESS.

For each candidate ceiling range, makes a small batch of practice audio with the AGC on, extracts
the features the network hears, and measures the key-down-over-key-up depth of the STRONG signals
(strength above one half). The range whose depths sit where the real recordings' do - 0.9 to 1.5,
median about 1.2 - is the one to train on.
"""

import os
import subprocess
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainLetters import read  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
BUILD = os.path.join(ROOT, "build")
TRAIN = os.path.join(ROOT, "training")


def depth(d):
    c = d[:, 2]
    return np.percentile(c, 90) - np.percentile(c, 15)


def real_depths():
    real, _ = read(os.path.join(TRAIN, "real.bin"))
    skip = {"beacons", "session1"}
    return [depth(d) for n, _, d, _ in real if n not in skip]


def batch(low, high, share=1.0, count=120, seed=901):
    folder = os.path.join(TRAIN, "cal")
    subprocess.run([os.path.join(BUILD, "MakeCtcAudio.exe"), os.path.join(ROOT, "recordings"), folder,
                    str(count), str(seed), "0.3", str(share), str(low), str(high)], check=True, capture_output=True)
    out = os.path.join(TRAIN, "cal.bin")
    subprocess.run([os.path.join(BUILD, "DumpFeatures.exe"), out, os.path.join(folder, "index.txt"), folder],
                   check=True, capture_output=True)
    strength = {}
    for line in open(os.path.join(folder, "index.txt"), encoding="utf-8"):
        b = line.rstrip("\n").split("\t")
        strength[b[0]] = float(b[3])
    examples, _ = read(out)
    return [depth(d) for n, t, d, _ in examples if t.strip() and strength.get(n, 0) > 0.5]


def show(label, values):
    v = np.array(values)
    print("  %-26s strong-signal depth: 10%% %.2f  median %.2f  90%% %.2f   (%d examples)"
          % (label, np.percentile(v, 10), np.median(v), np.percentile(v, 90), len(v)))


def main():
    show("REAL RECORDINGS", real_depths())
    show("practice, no AGC", batch(0.8, 1.6, share=0.0))
    for low, high in [(0.8, 1.6), (0.4, 1.2), (0.2, 0.9), (0.0, 0.7)]:
        show("practice, ceiling %.1f-%.1f" % (low, high), batch(low, high))


if __name__ == "__main__":
    main()
