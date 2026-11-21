"""
HOW STRONG IS A RECORDING, IN THE PRACTICE SOUND'S OWN dB (2026-10-27). gen.py sets every station's strength
as tone power over the noise in its own ~80 Hz (snr -3..38 dB). This measures a real recording the same way, so
the bench recordings can be laid beside the practice spread: if his weak session sits below what the network
practised on, train weaker; if not, its misses are something else (fading, a hand, QRM).

Per 15 s window, around Plain's note (<wav>.track): power in +-15 Hz, frame by frame; key down = its 90th
percentile, key up = its 20th. The reading is turned into gen.py's dB by a line fitted on practice stations of
known strength (no fading), made here first.

  signal_level.py <wav or folder> ...
"""
import os
import sys

import numpy as np
from scipy.io import wavfile
from scipy.signal import resample_poly, stft

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import RATE  # noqa: E402
import gen  # noqa: E402

WIN = 15.0


def raw_level(a, note):
    f, _, Z = stft(a, fs=RATE, nperseg=256, noverlap=192)
    p = (np.abs(Z) ** 2)[np.abs(f - note) <= 15].mean(axis=0)
    down, up = np.percentile(p, 90), np.percentile(p, 20)
    return 10 * np.log10(max(down - up, 1e-12) / (up + 1e-12))


def calibrate(count=200, seed=1027):
    """-> (slope, offset): gen dB = slope * raw + offset, from practice stations of known strength."""
    gen.load()
    rng = np.random.default_rng(seed)
    n = int(WIN * RATE)
    xs, ys = [], []
    for _ in range(count):
        noise = gen.noise_for(rng, n)
        psd = gen.Psd(noise)
        f0 = rng.uniform(400, 1000)
        wpm = rng.uniform(15, 30)
        snr = rng.uniform(-6, 30)
        text = gen.make_over_text(rng, WIN - 1.0, 1.2 / wpm)
        if not text:
            continue
        marks, _, _ = gen.keying(rng, text, wpm)
        marks = [(a + 0.5, b + 0.5) for a, b in marks]
        sig = gen.render_station(rng, marks, n, f0, wpm, 0.0, 0.01, 0.0)
        a = noise + np.sqrt(2 * 10 ** (snr / 10) * psd.at(f0) * 80.0) * sig
        xs.append(raw_level(a, f0)); ys.append(snr)
    xs, ys = np.array(xs), np.array(ys)
    ok = ys > 0                    # below 0 dB the reading flattens into the noise
    slope, offset = np.polyfit(xs[ok], ys[ok], 1)
    err = ys[ok] - (slope * xs[ok] + offset)
    print('calibration: %d stations, gen dB = %.2f * raw + %.1f, spread +-%.1f dB' % (ok.sum(), slope, offset, err.std()))
    return slope, offset


def track(path):
    if not os.path.exists(path + '.track'):
        return None
    t, hz = np.loadtxt(path + '.track', unpack=True, ndmin=2)
    return t, hz


def levels(path, slope, offset):
    rate, x = wavfile.read(path)
    if x.ndim > 1:
        x = x[:, 0]
    x = x.astype(np.float32)
    from math import gcd
    g = gcd(RATE, rate)
    a = resample_poly(x, RATE // g, rate // g)
    tr = track(path)
    out = []
    n = int(WIN * RATE)
    for s in range(0, len(a) - n + 1, n):
        if tr is None:
            continue
        t0, t1 = s / RATE, (s + n) / RATE
        hz = tr[1][(tr[0] >= t0) & (tr[0] < t1)]
        hz = hz[hz > 0]
        if len(hz) < 20:          # Plain heard nothing here: an empty stretch
            continue
        out.append(slope * raw_level(a[s:s + n], float(np.median(hz))) + offset)
    return out


def main():
    slope, offset = calibrate()
    for arg in sys.argv[1:]:
        files = [os.path.join(arg, f) for f in sorted(os.listdir(arg)) if f.lower().endswith('.wav')] \
            if os.path.isdir(arg) else [arg]
        for f in files:
            v = levels(f, slope, offset)
            if v:
                q = np.percentile(v, [10, 50, 90])
                print('%-60s windows %3d   dB 10%% %5.1f  median %5.1f  90%% %5.1f   under 8 dB %3d%%' % (
                    os.path.relpath(f), len(v), q[0], q[1], q[2], 100 * np.mean(np.array(v) < 8)))
    print('practice: 30% of stations -3..8 dB, 40% 8..20, 30% 20..38')


if __name__ == '__main__':
    main()
