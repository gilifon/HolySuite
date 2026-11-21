"""
REAL ON-AIR PRACTICE FOR THE NETWORK (2026-10-17): his station keyed by FistKeyer, heard by European KiwiSDRs.

FistKeyer wrote down the moment of every key-down and key-up (edges.tsv), so for each receiver's recording we know
exactly which letter was sent when - once we know where FistKeyer's clock sits in that recording. A piece's file
name gives its start to the second (and the receiver connects a little after), so the offset is FOUND, not trusted:
the keying, drawn as on/off in 15 ms frames, is slid along the recording note by note and the place where it matches
the sound best wins. Done on each half of a piece separately, so a receiver whose sample clock runs a little fast or
slow is caught too (the two halves then disagree).

  realdata.py align <session dir>              print the offset, tone and on/off contrast found for each piece
  realdata.py build <session dir> <out.npz>     cut labelled 15 s windows (see build())
"""
import glob
import os
import re
import sys
from datetime import datetime, timezone

import numpy as np
from scipy.signal import resample_poly

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *

FR = HOP / RATE                     # one spectrogram frame, 15 ms


def read_edges(session):
    """-> list of (part, text_index, letter, down_s, up_s) in FistKeyer's own time, and the parts' texts."""
    rows = []
    with open(os.path.join(session, 'edges.tsv'), encoding='utf-8') as f:
        next(f)
        for line in f:
            p, ti, c, _, _, d, u = line.rstrip('\n').split('\t')
            rows.append((int(p), int(ti), c, float(d) / 1000, float(u) / 1000))
    texts = [l.strip() for l in open(os.path.join(session, 'SENT.txt'), encoding='utf-8') if l.strip()]
    return rows, texts


def keyer_start(session):
    s = open(os.path.join(session, 'started_utc.txt'), encoding='utf-8').readline().strip()
    return datetime.strptime(s, '%Y-%m-%d %H:%M:%S.%f').replace(tzinfo=timezone.utc).timestamp()


def piece_start(path, day):
    """The UTC second in the file name (..._03_182938Z.wav), on the session's day."""
    hms = re.search(r'_(\d{6})Z\.wav$', path).group(1)
    return datetime.strptime(day + hms, '%Y-%m-%d%H%M%S').replace(tzinfo=timezone.utc).timestamp()


def load(path):
    import wave
    w = wave.open(path)
    rate = w.getframerate()
    x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(np.float32)
    from math import gcd
    g = gcd(RATE, rate)
    return resample_poly(x, RATE // g, rate // g).astype(np.float32)


def magnitude(a):
    pad = FFT // 2
    a = np.pad(a, (pad, pad), mode='reflect')
    frames = 1 + (len(a) - FFT) // HOP
    idx = np.arange(FFT)[None, :] + HOP * np.arange(frames)[:, None]
    return np.abs(np.fft.rfft(a[idx] * WINDOW, n=FFT, axis=1))[:, BIN0:BIN1]


def envelope(edges, t0, nframes):
    """Key-down as 1, in frames of the recording, when FistKeyer's time 0 is at t0 seconds into it."""
    e = np.zeros(nframes, np.float32)
    for _, _, _, d, u in edges:
        i, j = int(round((t0 + d) / FR)), int(round((t0 + u) / FR))
        if j <= 0 or i >= nframes:
            continue
        e[max(i, 0):min(j, nframes)] = 1
    return e


def best_offset(lm, K, guess, span=6.0, lo=0, hi=None, col=None):
    """Slide the keying K (drawn once, in FistKeyer's frames) along the log-magnitude [T, bins], scoring only
    recording frames lo..hi and, if given, only note column col; -> (offset s, bin, correlation)."""
    T = lm.shape[0]
    hi = T if hi is None else hi
    z = lm[lo:hi] if col is None else lm[lo:hi, col:col + 1]
    z = (z - z.mean(0)) / (z.std(0) + 1e-9)
    best = (-9.0, guess, 0)
    for k0 in range(int(round((guess - span) / FR)), int(round((guess + span) / FR)) + 1):
        e = np.zeros(T, np.float32)                     # recording frame i holds keyer frame i - k0
        a, b = max(0, k0), min(T, k0 + len(K))
        if b <= a:
            continue
        e[a:b] = K[a - k0:b - k0]
        e = e[lo:hi]
        if e.sum() < 20:
            continue
        ez = (e - e.mean()) / (e.std() + 1e-9)
        c = ez @ z / len(e)
        j = int(np.argmax(c))
        if c[j] > best[0]:
            best = (float(c[j]), k0 * FR, j if col is None else col)
    return best[1], best[2], best[0]


def keyer_frames(edges):
    return envelope(edges, 0.0, int(max(u for _, _, _, _, u in edges) / FR) + 2)


def align_piece(path, edges, K, k0, day):
    a = load(path)
    mag = magnitude(a)
    lm = np.log(mag + 1e-6)
    guess = k0 - piece_start(path, day)                # FistKeyer's time 0, in seconds into this recording
    t0, b, score = best_offset(lm, K, guess)
    if score < 0.3:     # a receiver can connect many seconds after the file's stamp (6.8 s, onair5 piece 05):
        wide = best_offset(lm, K, guess, 20.0)       # look further, and keep it only if it matches better
        if wide[2] > score:
            t0, b, score = wide
    # each half alone on the tone's own note, near the whole-piece answer: a receiver whose sample clock runs
    # fast or slow shows up as the two halves disagreeing
    T = lm.shape[0]
    halves = [best_offset(lm, K, t0, 0.3, lo, hi, b)[0] for lo, hi in ((0, T // 2), (T // 2, T))]
    e = envelope(edges, t0, T)
    tone = mag[:, b]
    span = np.nonzero(e)[0]
    contrast = None
    if len(span) > 20:
        s, f = span[0], span[-1]
        on, off = tone[s:f][e[s:f] > 0], tone[s:f][e[s:f] == 0]
        contrast = 20 * np.log10(np.median(on) / (np.median(off) + 1e-9))
    return dict(path=path, t0=t0, bin=b, hz=(BIN0 + b) * RATE / FFT, score=score, halves=halves, contrast=contrast, n=len(a))


def align(session):
    edges, _ = read_edges(session)
    k0 = keyer_start(session)
    day = open(os.path.join(session, 'started_utc.txt')).readline()[:10]
    K = keyer_frames(edges)
    out = []
    for p in sorted(glob.glob(os.path.join(session, 'rx', '*.wav'))):
        r = align_piece(p, edges, K, k0, day)
        out.append(r)
        h = ' '.join('-' if x is None else '%+.3f' % (x - r['t0']) for x in r['halves'])
        print("%-26s t0 %8.3f  %4.0f Hz  corr %.2f  halves %s  on/off %s dB" % (
            os.path.basename(p), r['t0'], r['hz'], r['score'], h, '-' if r['contrast'] is None else '%.1f' % r['contrast']))
        sys.stdout.flush()
    return out


if __name__ == '__main__':
    if sys.argv[1] == 'align':
        align(sys.argv[2])
