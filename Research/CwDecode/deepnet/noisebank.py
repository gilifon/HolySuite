"""
REAL BAND NOISE FOR THE PRACTICE SOUND: stretches of our own recordings with no keyed tone in them.

For every recording (the watcher clips, which are his IC-7610 through its CW filter, and the W1AW KiwiSDR
pieces), cut into 6 s blocks at 3200 Hz; a block is noise when no note in 300-1100 Hz stands up out of
its neighbours in more than a few frames ("peakiness"). Kept blocks go into noise_bank.npy as one array of
float16 [n, 6*3200] - each with its own source tag in noise_bank.txt so a bad source can be dropped - and
noise_kind.npy says which radio each came from (0 = his IC-7610, 1 = a KiwiSDR), so the practice sound can
keep his own radio's share up however many Kiwi blocks there are.

2026-10-17, second run: the nightly W1AW Europe recordings added (all nights, a few blocks per file), and the
W1AW recordings we TEST on (20260915/20260916) left out - the first bank took its Kiwi blocks from them.

  noisebank.py
"""
import glob
import os
import sys
import wave
import numpy as np
from math import gcd
from scipy.signal import resample_poly

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *

CD = os.path.join(HERE, '..')
BLOCK = 6 * RATE


def read(path):
    with wave.open(path, 'rb') as w:
        rate, ch, n = w.getframerate(), w.getnchannels(), w.getnframes()
        x = np.frombuffer(w.readframes(n), dtype='<i2').astype(np.float32) / 32768.0
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    g = gcd(rate, RATE)
    return resample_poly(x, RATE // g, rate // g).astype(np.float32) if rate != RATE else x


def peakiness(block):
    """How far the loudest note stands above ITS OWN NEIGHBOURS (13 bins, 160 Hz), in the loud frames.
    Against its neighbours, not the whole band: his IC-7610's CW filter makes a hump of noise, and the
    whole-band median called every block of that noise a station."""
    s = np.abs(np.fft.rfft(np.lib.stride_tricks.sliding_window_view(block, FFT)[::HOP] * WINDOW, axis=1))[:, BIN0:BIN1]
    p = s ** 2
    pad = np.pad(p, ((0, 0), (6, 6)), mode='edge')
    local = np.median(np.lib.stride_tricks.sliding_window_view(pad, 13, axis=1), axis=2) + 1e-12
    strongest = (p / local).max(axis=1)
    return float(np.percentile(strongest, 90))


def main():
    own = sorted(glob.glob(os.path.join(CD, 'recordings', 'watch*', '*.wav')) + glob.glob(os.path.join(CD, 'recordings', 'live1', '*.wav')))
    kiwi = sorted(glob.glob(os.path.join(CD, 'training', 'w1aw', '*-europe', '*.wav')))
    files = [(f, 0) for f in own] + [(f, 1) for f in kiwi]
    keep, tags, kinds = [], [], []
    seen = []
    for f, kind in files:
        x = read(f)
        k = 0
        for s in range(0, len(x) - BLOCK, BLOCK):
            b = x[s:s + BLOCK]
            if np.abs(b).max() < 1e-4:
                continue
            pk = peakiness(b)
            seen.append(pk)
            if pk < PEAK_LIMIT and k < (PER_FILE_MAX if kind == 0 else PER_KIWI_FILE_MAX):
                keep.append(b / (b.std() + 1e-9)); tags.append(os.path.basename(f)); kinds.append(kind); k += 1
        print("%-50s kept %d" % (os.path.basename(f), k), flush=True)
    np.save(os.path.join(HERE, 'noise_bank.npy'), np.array(keep, dtype=np.float16))
    np.save(os.path.join(HERE, 'noise_kind.npy'), np.array(kinds, dtype=np.int8))
    print("his IC-7610: %d blocks, KiwiSDR: %d blocks" % (kinds.count(0), kinds.count(1)))
    open(os.path.join(HERE, 'noise_bank.txt'), 'w').write('\n'.join(tags))
    seen = np.array(seen)
    print("blocks seen %d, kept %d; peakiness percentiles 5/25/50/75/95: %s" % (len(seen), len(keep), np.percentile(seen, [5, 25, 50, 75, 95]).round(1)))


PER_FILE_MAX = 20
PER_KIWI_FILE_MAX = 4
PEAK_LIMIT = float(os.environ.get('PEAK_LIMIT', '26'))      # both banks were built with 26; at 14 not one block of his IC-7610's survived

if __name__ == '__main__':
    main()
