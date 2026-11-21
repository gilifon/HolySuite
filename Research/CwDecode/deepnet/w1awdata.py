"""
W1AW'S EVENING BULLETIN, HEARD IN EUROPE, AS REAL PRACTICE FOR THE NETWORK (2026-10-19).

Every night a scheduled task records W1AW's 00:00 UTC code bulletin from several European KiwiSDRs
(training/w1aw/<date>-europe). ARRL publishes the text (training/w1aw/texts/ARLxnnn.txt). That makes real CW
from another station, over a real Atlantic path, with known text - what the on-air sessions of his own station
cannot give.

THE PUBLISHED TEXT IS NOT WORD FOR WORD WHAT IS SENT: on the air a hyphen is the word DASH, apostrophes and
brackets go, paragraphs become =, "QST DE W1AW" is dropped in every few minutes and the web addresses are cut.
So the text is never trusted alone. The night's STRONGEST copy is read by the network, and the words it read
are matched to the published words (difflib). A word counts only where the two agree - exactly, or the network
was at most two letters off a word of the same length (then the PUBLISHED spelling is the label: that is where
it learns). A window is 15 s that starts and ends in a word gap the network heard, and every word in it must
count, in order, with nothing of the published text skipped.

THE OTHER RECEIVERS of the same night heard the same keying, weaker or fading. Each of their pieces is slid
along the strong copy's keying (tone on/off) to find where it sits; the same windows, with the same labels,
are then cut from them - that is the real gain: weak copies whose text is known.

  w1awdata.py <night dir> [<night dir> ...] --out windows.json      label nights (slow: the network reads them)
  w1awdata.py cut windows.json <out prefix>                         cut the windows' audio into <out>.npy + <out>.json
"""
import argparse
import difflib
import glob
import json
import os
import re
import sys
from datetime import datetime, timezone

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *
import realdata

SEC = 15.0
FR = realdata.FR
TEXTS = os.path.join(HERE, '..', 'training', 'w1aw', 'texts')


# ------------------------------------------------------------------ the network reading a whole piece
def timeline(a, decode_window):
    """-> [(char, seconds)] for a whole piece: 15 s windows every 5 s, each keeping its middle 5 s."""
    n = int(SEC * RATE)
    total = len(a) / RATE
    out = []
    start = 0.0
    while start < total - 1.0:
        s0 = int(start * RATE)
        seg = a[s0:s0 + n]
        if len(seg) < n:
            seg = np.pad(seg, (0, n - len(seg)))
        lo = 0.0 if start == 0.0 else 5.0
        hi = 1e9 if start + SEC >= total else 10.0
        for ch, t in decode_window(seg):
            if lo <= t < hi:
                out.append((ch, start + t))
        start += 5.0
    return out


def words_of(tl):
    """Timeline -> words [(text, first char time, last char time, char times)] and the space times."""
    words, spaces, cur = [], [], []
    for ch, t in tl:
        if ch == ' ':
            if cur:
                words.append((''.join(c for c, _ in cur), cur[0][1], cur[-1][1], [x for _, x in cur]))
                cur = []
            spaces.append(t)
        else:
            cur.append((ch, t))
    if cur:
        words.append((''.join(c for c, _ in cur), cur[0][1], cur[-1][1], [x for _, x in cur]))
    return words, np.array(spaces)


# ------------------------------------------------------------------ the published text, as sent
def published_words(code):
    t = open(os.path.join(TEXTS, code + '.txt'), encoding='utf-8', errors='replace').read().upper()
    t = t.replace('NNNN', '')
    t = re.sub(r'HTTPS?://\S+|WWW\.\S+', ' ', t)
    t = re.sub(r'\n\s*\n', ' = ', t)                      # a paragraph is sent as =
    t = t.replace('-', ' DASH ')
    t = re.sub(r"[’'()\[\]\"“”:;]", '', t)
    t = re.sub(r'\.(\s|$)', r' . \1', t)                   # a full stop is its own word on the air
    words = []
    for w in t.split():
        if all(c in C2I for c in w):
            words.append(w)
    return words


def edit(a, b):
    d = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        prev, d[0] = d[0], i
        for j in range(1, len(b) + 1):
            cur = d[j]
            d[j] = min(d[j] + 1, d[j - 1] + 1, prev + (a[i - 1] != b[j - 1]))
            prev = cur
    return d[len(b)]


def match(words, ref):
    """-> for each decoded word, the index of the published word it counts as (or None)."""
    sm = difflib.SequenceMatcher(None, [w[0] for w in words], ref, autojunk=False)
    got = [None] * len(words)
    for op, i1, i2, j1, j2 in sm.get_opcodes():
        if op == 'equal':
            for k in range(i2 - i1):
                got[i1 + k] = j1 + k
        elif op == 'replace' and i2 - i1 == j2 - j1:
            for k in range(i2 - i1):
                a, b = words[i1 + k][0], ref[j1 + k]
                if len(a) == len(b) and edit(a, b) <= 2 and len(b) >= 3:
                    got[i1 + k] = j1 + k
    return got


# ------------------------------------------------------------------ the windows of the strong copy
def windows_from(words, spaces, got, ref):
    """Windows [s, s+15) in the strong copy's time: both ends at a heard word gap, every word inside counted,
    the counted published words consecutive. -> [(s, label, events)]"""
    out = []
    starts = sorted(set(round(x + d, 3) for x in spaces for d in (-0.15, 0.0, 0.15)))
    for s in starts:
        e = s + SEC
        j = np.searchsorted(spaces, e)
        near = [x for x in spaces[max(0, j - 1):j + 1] if abs(x - e) < 0.2]
        if not near:
            continue
        inside = [k for k, w in enumerate(words) if w[1] > s and w[2] < e]
        partial = [k for k, w in enumerate(words) if (w[1] < s < w[2]) or (w[1] < e < w[2])]
        if len(inside) < 3 or partial:
            continue
        idx = [got[k] for k in inside]
        if any(i is None for i in idx) or any(b != a + 1 for a, b in zip(idx, idx[1:])):
            continue
        label = ' '.join(ref[i] for i in idx)
        ev = []
        for n, k in enumerate(inside):
            w = words[k]
            if n > 0:
                ev.append((0.5 * (words[inside[n - 1]][2] + w[1]) - s, ' '))
            r = ref[got[k]]
            ts = w[3] if len(w[3]) == len(r) else list(np.linspace(w[1], w[2], len(r)))
            ev += [(t - s, c) for t, c in zip(ts, r)]
        out.append((float(s), label, ev))
    return out


# ------------------------------------------------------------------ the other receivers
def stamp(path):
    """UTC seconds of a piece's start from its name (..._01_235700Z.wav) and its night folder (local date, +3 h)."""
    hms = re.search(r'_(\d{6})Z\.wav$', path).group(1)
    folder = os.path.basename(os.path.dirname(path))[:8]
    day = datetime.strptime(folder, '%Y%m%d').replace(tzinfo=timezone.utc).timestamp()
    t = day + int(hms[:2]) * 3600 + int(hms[2:4]) * 60 + int(hms[4:])
    if hms[:2] >= '12':
        t -= 86400                                         # 23:57 belongs to the evening before the folder's date
    return t


def keying_of(mag_col):
    """On/off of the strong copy's tone, thresholded halfway between its quiet and loud levels (log)."""
    lm = np.log(mag_col + 1e-6)
    lo, hi = np.percentile(lm, 25), np.percentile(lm, 95)
    return (lm > 0.5 * (lo + hi)).astype(np.float32)


def offset_in(lm, K, k0_guess, span_s=12.0):
    """Where in the strong copy's frames this piece starts: slide K along lm [T, bins]. -> (frame, bin, corr)."""
    T = lm.shape[0]
    z = (lm - lm.mean(0)) / (lm.std(0) + 1e-9)
    best = (-9.0, 0, 0)
    span = int(span_s / FR)
    for k0 in range(k0_guess - span, k0_guess + span + 1):
        # piece frame i holds strong-copy frame i + k0
        a, b = max(0, -k0), min(T, len(K) - k0)
        if b - a < 200:
            continue
        e = np.zeros(T, np.float32)
        e[a:b] = K[a + k0:b + k0]
        if e.sum() < 50:
            continue
        ez = (e - e.mean()) / (e.std() + 1e-9)
        c = ez @ z / T
        j = int(np.argmax(c))
        if c[j] > best[0]:
            best = (float(c[j]), k0, j)
    return best[1], best[2], best[0]


def label_night(night, decode_window, codes):
    """-> list of windows {path, s, label, events, db, code} for every receiver of one night."""
    pieces = sorted(glob.glob(os.path.join(night, '*.wav')))
    by_rx = {}
    for p in pieces:
        by_rx.setdefault(re.sub(r'_\d+_\d{6}Z\.wav$', '', os.path.basename(p)), []).append(p)
    def lift(path):
        a = realdata.load(path)
        m = 20 * np.log10(realdata.magnitude(a[:int(240 * RATE)]) + 1e-6)
        l = np.percentile(m, 95, axis=0) - np.percentile(m, 30, axis=0)
        return float(l.max()), int(np.argmax(l))
    # THE STRONG COPY: not the loudest note (that picked another station once) but the receiver whose reading of
    # its second piece (00:07-00:17, the bulletin's body) agrees with a published bulletin on the most words
    refs = {c: published_words(c) for c in codes}
    score = {}
    for rx, ps in by_rx.items():
        p = ps[1] if len(ps) > 1 else ps[0]
        w, _ = words_of(timeline(realdata.load(p), decode_window))
        score[rx] = max(sum(x is not None for x in match(w, r)) for r in refs.values()) if w else 0
    strong = max(score, key=score.get)
    print("  %s: agreeing words per receiver %s" % (os.path.basename(night), score))
    if score[strong] < 40:
        print("  %s: no receiver read the bulletin" % os.path.basename(night))
        return []
    # the strong copy as one timeline, in UTC seconds
    t_base = stamp(by_rx[strong][0])
    allw, alls, K_parts = [], [], []
    tone_bin = lift(by_rx[strong][0])[1]
    audio_strong = {}
    for p in by_rx[strong]:
        a = realdata.load(p)
        audio_strong[p] = a
        off = stamp(p) - t_base
        w, sp = words_of(timeline(a, decode_window))
        allw += [(x, s1 + off, s2 + off, [t + off for t in ts]) for x, s1, s2, ts in w]
        alls += list(sp + off)
    alls = np.array(sorted(alls))
    text = ' '.join(w[0] for w in allw)
    # which bulletin: the published text the network's reading agrees with most
    best = None
    for c in codes:
        r = published_words(c)
        g = match(allw, r)
        n = sum(x is not None for x in g)
        if best is None or n > best[0]:
            best = (n, c, r, g)
    _, code, ref, got = best
    wins = windows_from(allw, alls, got, ref)
    counted = sum(g is not None for g in got)
    print("  %s: strong copy %s, bulletin %s, %d of %d words counted, %d windows" % (
        os.path.basename(night), strong, code, counted, len(allw), len(wins)))
    # keying of the strong copy over the whole night, in frames from t_base
    total = int((stamp(by_rx[strong][-1]) - t_base + len(audio_strong[by_rx[strong][-1]]) / RATE) / FR) + 1
    K = np.zeros(total, np.float32)
    for p, a in audio_strong.items():
        k0 = int(round((stamp(p) - t_base) / FR))
        kk = keying_of(realdata.magnitude(a)[:, tone_bin])
        K[k0:k0 + len(kk)] = np.maximum(K[k0:k0 + len(kk)], kk[:max(0, total - k0)])
    out = []
    for rx, ps in by_rx.items():
        for p in ps:
            a = audio_strong.get(p)
            if a is None:
                a = realdata.load(p)
            mag = realdata.magnitude(a)
            if rx == strong:
                k0, b, c = int(round((stamp(p) - t_base) / FR)), tone_bin, 1.0
            else:
                k0, b, c = offset_in(np.log(mag + 1e-6), K, int(round((stamp(p) - t_base) / FR)))
            if c < 0.2:
                continue
            dur = len(a) / RATE
            col = mag[:, b]
            for s, label, ev in wins:
                ls = s - k0 * FR                          # where the window starts in this piece
                if ls < 0 or ls + SEC > dur:
                    continue
                i0, i1 = int(ls / FR), int((ls + SEC) / FR)
                kw = K[k0 + i0:k0 + i1]
                seg = col[i0:i1][:len(kw)]
                on, offv = seg[kw[:len(seg)] > 0], seg[kw[:len(seg)] == 0]
                if len(on) < 20 or len(offv) < 20:
                    continue
                db = 20 * np.log10(np.median(on) / (np.median(offv) + 1e-9))
                out.append(dict(path=p, s=float(ls), label=label, events=ev, db=float(db), code=code, rx=rx))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('nights', nargs='+')
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    import own
    codes = sorted(os.path.basename(f)[:-4] for f in glob.glob(os.path.join(TEXTS, 'ARL*.txt')))
    allwin = []
    for n in a.nights:
        try:
            allwin += label_night(n, own.decode_window, codes)
        except Exception as e:
            print("  %s: failed - %s" % (os.path.basename(n), e))
        sys.stdout.flush()
        json.dump(allwin, open(a.out + '.tmp', 'w'), default=float)
        os.replace(a.out + '.tmp', a.out)
    print("%d windows" % len(allwin))




# ------------------------------------------------------------------ cutting the windows out once
def cut(json_path, out_prefix, min_db=3.0):
    """All labelled windows' audio into one float16 file (<out>.f16, [n, 15 s at RATE]) beside a json of their
    labels - so training reads a slice instead of loading and resampling a 10-minute piece each time."""
    wins = [w for w in json.load(open(json_path)) if w['db'] >= min_db]
    n = int(SEC * RATE)
    mm = np.lib.format.open_memmap(out_prefix + '.npy', mode='w+', dtype=np.float16, shape=(len(wins), n))
    cache = {}
    for i, w in enumerate(wins):
        if w['path'] not in cache:
            cache.clear()
            cache[w['path']] = realdata.load(w['path'])
        a = cache[w['path']]
        i0 = int(round(w['s'] * RATE))
        x = a[i0:i0 + n].astype(np.float32)
        x = x / (x.std() + 1e-9)
        mm[i, :len(x)] = x.astype(np.float16)
    mm.flush()
    meta = [dict(label=w['label'], events=w['events'], db=w['db'], code=w['code'], rx=w['rx'],
                 night=os.path.basename(os.path.dirname(w['path']))) for w in wins]
    json.dump(meta, open(out_prefix + '.json', 'w'), default=float)
    print("%d windows cut to %s.npy" % (len(wins), out_prefix))


if __name__ == '__main__':
    if sys.argv[1] == 'cut':
        cut(sys.argv[2], sys.argv[3])
    else:
        main()
