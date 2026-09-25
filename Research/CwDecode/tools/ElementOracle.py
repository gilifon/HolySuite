"""
HOW WELL COULD A WHOLE-ELEMENT DECODER READ THIS, IF IT KNEW THE SPEED?

The fist session (recordings/fist1) is weak - 4 to 8 dB over the band in 20 ms readings - yet a whole
dit or dah stands 8 to 13 dB above the gap beside it (measured from the exact key log). The plain
decoder never believes a station is there, and letting it believe only reads 6-9 words: it judges
every 5 ms on its own. ElementDecoder.py judged whole elements but took its speed from the plain
decoder's marks, which are rubbish on a signal this weak.

This separates the two questions. It measures the loudness itself, at the file's own pitch (found by
CutFist.py), and is TOLD the speed - the median dit of each part, from edges.tsv. What it then reads
is the ceiling of the element-level idea on this signal; whatever is lost after that belongs to speed
finding, which is a different job.

Only Morse's own timing is used. No words, no dictionary.

  ElementOracle.py <bench folder> [--truth-speed | --speed WPM]
"""

import csv
import os
import sys
import wave

import numpy as np

MORSE = {
    '.-': 'A', '-...': 'B', '-.-.': 'C', '-..': 'D', '.': 'E', '..-.': 'F', '--.': 'G', '....': 'H', '..': 'I',
    '.---': 'J', '-.-': 'K', '.-..': 'L', '--': 'M', '-.': 'N', '---': 'O', '.--.': 'P', '--.-': 'Q', '.-.': 'R',
    '...': 'S', '-': 'T', '..-': 'U', '...-': 'V', '.--': 'W', '-..-': 'X', '-.--': 'Y', '--..': 'Z',
    '-----': '0', '.----': '1', '..---': '2', '...--': '3', '....-': '4', '.....': '5', '-....': '6',
    '--...': '7', '---..': '8', '----.': '9', '-..-.': '/', '-...-': '=', '..--..': '?', '.-.-.-': '.',
    '--..--': ',',
}

HOP = 0.005
# (is_mark, units, name); a word gap is "7 or more"
KINDS = [(True, 1.0, 'dit'), (True, 3.0, 'dah'), (False, 1.0, 'egap'), (False, 3.0, 'lgap'), (False, 7.0, 'wgap')]
DUR_SD = 0.30
LONGEST_GAP_UNITS = 60


def loudness(x, sr, tone, window=0.010):
    """Level in dB at the tone, every 5 ms, over a short window."""
    n = int(window * sr); h = int(HOP * sr)
    c = np.exp(-2j * np.pi * tone * np.arange(n) / sr) * np.hanning(n)
    frames = np.lib.stride_tricks.sliding_window_view(x, n)[::h]
    return 20 * np.log10(np.abs(frames @ c) + 1e-3)


def local_levels(db, span_frames=2000):
    """Noise and tone levels around each frame: low and high percentiles over +-5 s."""
    T = len(db); lo = np.empty(T); hi = np.empty(T)
    step = 200
    for c in range(0, T, step):
        seg = db[max(0, c - span_frames):c + span_frames]
        lo[c:c + step] = np.percentile(seg, 30)
        hi[c:c + step] = np.percentile(seg, 92)
    return lo, hi


def decode(db, unit_frames):
    T = len(db)
    lo, hi = local_levels(db)
    spread = np.maximum((hi - lo) / 2.5, 1.0)
    ll1 = -0.5 * ((db - hi) / spread) ** 2
    ll0 = -0.5 * ((db - lo) / spread) ** 2
    # tone can be louder than "hi" and silence quieter than "lo" without penalty
    ll1 = np.where(db > hi, 0.0, ll1)
    ll0 = np.where(db < lo, 0.0, ll0)
    c1 = np.concatenate([[0.0], np.cumsum(ll1)])
    c0 = np.concatenate([[0.0], np.cumsum(ll0)])

    NEG = -1e18
    best = np.full((T + 1, len(KINDS)), NEG)
    back = np.zeros((T + 1, len(KINDS), 2), dtype=np.int32)
    best[0, 4] = 0.0
    allowed = {0: [2, 3, 4], 1: [2, 3, 4], 2: [0, 1], 3: [0, 1], 4: [0, 1]}
    for t in range(1, T + 1):
        u = unit_frames[t - 1]
        for k, (is_mark, units, _) in enumerate(KINDS):
            want = units * u
            lo_d = max(1, int(want * np.exp(-3 * DUR_SD)))
            hi_d = int(want * np.exp(3 * DUR_SD)) + 1 if k != 4 else int(LONGEST_GAP_UNITS * u)
            hi_d = min(hi_d, t)
            if hi_d < lo_d:
                continue
            d = np.arange(lo_d, hi_d + 1)
            starts = t - d
            prev = best[starts][:, allowed[k]]
            pb = prev.max(axis=1); pa = np.array(allowed[k])[prev.argmax(axis=1)]
            emit = (c1[t] - c1[starts]) if is_mark else (c0[t] - c0[starts])
            ld = np.log(d / want)
            dur = np.where(ld < 0, -0.5 * (ld / DUR_SD) ** 2, 0.0) if k == 4 else -0.5 * (ld / DUR_SD) ** 2
            score = pb + emit + dur
            i = int(np.argmax(score))
            if score[i] > best[t, k]:
                best[t, k] = score[i]; back[t, k] = (starts[i], pa[i])
    k = int(np.argmax(best[T])); t = T; segs = []
    while t > 0:
        s0, pk = back[t, k]
        segs.append(KINDS[k][2]); t, k = int(s0), int(pk)
    segs.reverse()
    out, letter = [], ''
    for name in segs:
        if name == 'dit': letter += '.'
        elif name == 'dah': letter += '-'
        else:
            if name in ('lgap', 'wgap') and letter:
                out.append(MORSE.get(letter, '')); letter = ''
            if name == 'wgap': out.append(' ')
    if letter: out.append(MORSE.get(letter, ''))
    return ' '.join(''.join(out).split())


def main(folder, mode="--truth-speed", wpm=None):
    rows = [r for r in csv.DictReader(open(os.path.join(folder, "edges.tsv")), delimiter="\t") if r["actual_down_ms"] != "nan"]
    offs = list(csv.DictReader(open(os.path.join(folder, "offsets.tsv")), delimiter="\t"))
    # the true dit of each part: the median of the short marks it actually keyed
    parts = sorted({int(r["part"]) for r in rows})
    ditms = {}; span = {}
    for p in parts:
        pr = [r for r in rows if int(r["part"]) == p]
        lens = np.array([float(r["actual_up_ms"]) - float(r["actual_down_ms"]) for r in pr])
        short = lens[lens < 2 * np.min(lens) + 10]
        ditms[p] = float(np.median(short))
        span[p] = (float(pr[0]["actual_down_ms"]) / 1000, float(pr[-1]["actual_up_ms"]) / 1000)

    out = open(os.path.join(folder, "..", "..", "build", "oracle_%s.txt" % os.path.basename(folder)), "w", encoding="utf-8")
    for o in offs:
        w = wave.open(os.path.join(folder, o["file"])); sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float); w.close()
        db = loudness(x, sr, float(o["tone_hz"]))
        zero = float(o["zero_s"])
        unit = np.empty(len(db))
        # per frame: the dit of the part being sent then (between parts, the next part's)
        t = np.arange(len(db)) * HOP - zero
        unit[:] = ditms[parts[-1]]
        for p in reversed(parts):
            unit[t < span[p][1] + 2] = ditms[p]
        if mode == "--speed":
            unit[:] = 1200.0 / float(wpm)
        unit_frames = unit / 1000 / HOP
        text = decode(db, unit_frames)
        out.write("%s\t%s\n" % (o["file"][:-4], text))
        print("%-20s %s" % (o["file"][:-4], text[:160]))
        sys.stdout.flush()
    out.close()


if __name__ == "__main__":
    main(*sys.argv[1:])
