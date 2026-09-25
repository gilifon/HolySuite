"""
THE WHOLE-ELEMENT DECODER WITHOUT THE ANSWER SHEET.

ElementCoherent.py was told four things from the key log: the exact pitch, the noise power, the tone
strength and the speed. A decoder in HolyLogger has none of them, so here each comes from the audio:

  pitch   the note at which 40 ms pieces of the signal add up loudest at their loudest moments -
          a tone keeps its phase for that long and noise does not - searched to a quarter of a Hz
          around the note the plain decoder's filter bank would pick
  noise   over +-5 s, the quiet 30% of the 5 ms readings: CW is key-up most of the time, and the
          power of noise alone is exponential, so its 30th percentile is 0.357 of its mean
  tone    over +-5 s, the loud end (85th percentile) of 40 ms coherent pieces, with the noise that
          comes with them taken out
  speed   over 20 s stretches, read loosely at 20 WPM, the dit taken from the marks that reading
          drew, then read again at that speed (see speed())

The key log is used only to COUNT the mistakes afterwards, exactly as CoherentErrors.py counts them.

  ElementBlind.py <bench folder> <file part of name> [seconds, default all]
"""

import csv
import os
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ElementCoherent as E

E.CHUNK = 4
HOP = E.HOP


def local(v, fn, half=1000, step=200):
    out = np.empty(len(v))
    for c in range(0, len(v), step):
        out[c:c + step] = fn(v[max(0, c - half):c + half])
    return out


def coherent_sum(z, c):
    C = np.concatenate([[0j], np.cumsum(z)])
    S = C[c:] - C[:-c]
    return np.concatenate([np.full(c // 2, S[0]), S, np.full(len(z) - len(S) - c // 2, S[-1])])[:len(z)]


def find_pitch(x, sr, rough):
    def loud(p):
        return np.percentile(np.abs(coherent_sum(E.mixed_frames(x, sr, p), 8)) ** 2, 95)
    g = np.arange(rough - 12, rough + 12.01, 1.0)
    p1 = g[np.argmax([loud(p) for p in g])]
    g = np.arange(p1 - 1, p1 + 1.01, 0.25)
    return g[np.argmax([loud(p) for p in g])]


def levels(z):
    noise = local(np.abs(z) ** 2, lambda s: np.percentile(s, 30) / 0.357)
    c = 8
    e8 = np.abs(coherent_sum(z, c)) ** 2
    tone = np.maximum((local(e8, lambda s: np.percentile(s, 85)) - c * noise) / (c * c), noise * 0.05)
    # THE STATION'S STRENGTH IS REMEMBERED THROUGH A PAUSE. Measured only locally, it sank to the noise
    # in every silence between overs, where "tone" and "no tone" then looked alike and noise was read
    # as E I E EE - most of the invented words on session 3. Held at half of the strongest seen within
    # HOLD frames either side, a silence is judged against the station as it was, and reads as silence.
    if HOLD:
        step = 200
        held = np.array([tone[max(0, i - HOLD):i + HOLD].max() for i in range(0, len(tone), step)])
        tone = np.maximum(tone, HOLD_SHARE * np.repeat(held, step)[:len(tone)])
    return np.sqrt(tone), noise


HOLD = 2000          # frames either side (10 s); 0 = off
HOLD_SHARE = 0.5


def speed(z, amp, noise, window=4000, hop=2000):
    """Frames per dit around each frame, found by the decoder itself: read a 20 s stretch loosely at
    20 WPM, take the dit from the marks it drew (a short and a long group in log length; the short
    group's middle), read again at that speed, and once more. Choosing the speed with the best score
    instead was tried and picks the slowest speed offered - a slow reading needs fewer, longer pieces."""
    unit = np.full(len(z), 12.0)
    keep_sd = E.DUR_SD
    for start in range(0, max(1, len(z) - hop), hop):
        sl = slice(start, min(len(z), start + window)); n = sl.stop - sl.start
        if n < 400: break
        g = 12.0
        for it in range(3):
            E.DUR_SD = 0.5 if it == 0 else 0.35
            _, segs = E.decode(z[sl], np.full(n, g), amp[sl], noise[sl])
            L = np.log([b - a for k, a, b in segs if k in ('dit', 'dah')] or [g])
            lo, hi = np.percentile(L, 20), np.percentile(L, 80)
            for _ in range(15):
                mid = (lo + hi) / 2; A, Bb = L[L < mid], L[L >= mid]
                if len(A) == 0 or len(Bb) == 0: break
                lo, hi = A.mean(), Bb.mean()
            g = min(max(np.exp(lo), 5.0), 35.0)
        a0 = start + (window - hop) // 2 if start else 0
        unit[a0:] = g
    E.DUR_SD = keep_sd
    return unit


WORD_GATE = 0.6     # a word is shown only if its marks stood out by this much evidence per frame


def gated_text(segs, F, gate=None):
    """The decoded text, keeping only words whose dits and dahs stood out - see WORD_GATE.

    MEASURED on session 3 before it was set: at 0.6 every correctly read word stayed (57 of 57 and 35
    of 35 on two receivers) while half to two-thirds of the invented ones went - nearly all of them
    the E I E EE the decoder drew out of the silences between overs, where nothing stands out."""
    gate = WORD_GATE if gate is None else gate
    out, letter, word, ev = [], '', '', []

    def flush():
        nonlocal word, ev
        if word and (not ev or np.mean(ev) >= gate):
            out.append(word)
        word, ev = '', []
    for n, a, b in segs:
        if n in ('dit', 'dah'):
            letter += '.' if n == 'dit' else '-'
            ev.append((F[b] - F[a]) / (b - a))
        else:
            if n in ('lgap', 'wgap') and letter:
                word += E.MORSE.get(letter, ''); letter = ''
            if n == 'wgap':
                flush()
    if letter:
        word += E.MORSE.get(letter, '')
    flush()
    return ' '.join(out)


def main(folder, only, seconds=None):
    rows = [r for r in csv.DictReader(open(os.path.join(folder, "edges.tsv")), delimiter="\t") if r["actual_down_ms"] != "nan"]
    o = [r for r in csv.DictReader(open(os.path.join(folder, "offsets.tsv")), delimiter="\t") if only in r["file"]][0]
    w = wave.open(os.path.join(folder, o["file"])); sr = w.getframerate()
    x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float); w.close()

    # the plain decoder's bank would land within 25 Hz; start from the tone CutFist saw, rounded to 25
    rough = round(float(o["tone_hz"]) / 25) * 25
    pitch = find_pitch(x, sr, rough)
    z = E.mixed_frames(x, sr, pitch)
    amp, noise = levels(z)
    unit = speed(z, amp, noise)

    T = len(z) if not seconds else min(len(z), int((float(o["zero_s"]) + 1.5 + float(seconds)) / HOP))
    text, segs = E.decode(z[:T], unit[:T], amp[:T], noise[:T])

    # count, against the key log
    zero = float(o["zero_s"]); scale = 1 + float(o.get("drift_ppm") or 0) / 1e6
    downs = np.array([float(r["actual_down_ms"]) / 1000 for r in rows]) * scale
    ups = np.array([float(r["actual_up_ms"]) / 1000 for r in rows]) * scale
    part = np.array([int(r["part"]) for r in rows])
    ea = ((zero + downs) / HOP).astype(int); eb = ((zero + ups) / HOP).astype(int)
    true_unit = {p: np.median(np.sort(eb[part == p] - ea[part == p])[:max(3, (part == p).sum() // 2)]) for p in set(part)}
    true_dah = np.array([(eb[i] - ea[i]) > 2 * true_unit[part[i]] for i in range(len(rows))])
    inside = eb < T
    found = np.zeros(len(rows), bool); false_marks = wrong = merged = 0
    for n, a, b in segs:
        if n not in ('dit', 'dah'): continue
        ov = np.flatnonzero(inside & (ea < b) & (eb > a))
        if len(ov) == 0: false_marks += 1; continue
        if len(ov) > 1: merged += 1
        found[ov[0]] = True
        if (n == 'dah') != true_dah[ov[0]]: wrong += 1
    missed = (inside & ~found).sum()
    tu = [np.median(unit[ea[part == p][ea[part == p] < T]]) for p in sorted(set(part)) if (ea[part == p] < T).any()]
    print("%-18s pitch %.2f (truth %s)  unit found %s  truth %s" % (o["file"][:-4], pitch, o["tone_hz"],
          " ".join("%.0f" % v for v in tu), " ".join("%.0f" % true_unit[p] for p in sorted(true_unit))))
    print("  %d real elements: missed %d  false %d  wrong kind %d  merged %d  -> %.1f%% wrong" % (
          inside.sum(), missed, false_marks, wrong, merged, 100.0 * (missed + false_marks + wrong + merged) / inside.sum()))
    print("  " + text[:300])
    out = os.path.join(folder, "..", "..", "build", "blind_%s_%s.txt" % (os.path.basename(folder), only))
    open(out, "w", encoding="utf-8").write("%s\t%s\n" % (o["file"][:-4], text))


if __name__ == "__main__":
    main(*sys.argv[1:])
