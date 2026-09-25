"""
WHICH MISTAKES DOES ElementCoherent.py MAKE? Its marks against the key log, one by one.

For every mark it found: does it overlap a real element, and is it the right kind (dit/dah)?
For every real element: was it found at all?

  CoherentErrors.py <bench folder> <file part of name>
"""

import csv
import os
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ElementCoherent as E


def main(folder, only):
    rows = [r for r in csv.DictReader(open(os.path.join(folder, "edges.tsv")), delimiter="\t") if r["actual_down_ms"] != "nan"]
    o = [r for r in csv.DictReader(open(os.path.join(folder, "offsets.tsv")), delimiter="\t") if only in r["file"]][0]
    w = wave.open(os.path.join(folder, o["file"])); sr = w.getframerate()
    x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float); w.close()
    zero = float(o["zero_s"])
    parts = sorted({int(r["part"]) for r in rows})
    downs = np.array([float(r["actual_down_ms"]) / 1000 for r in rows]); ups = np.array([float(r["actual_up_ms"]) / 1000 for r in rows])
    scale = 1 + float(o.get("drift_ppm") or 0) / 1e6          # the receiver's clock drifts - see CutFist.py
    part_of = np.array([int(r["part"]) for r in rows])
    ditms, span = {}, {}
    for p in parts:
        lens = (ups - downs)[part_of == p] * 1000
        ditms[p] = float(np.median(lens[lens < 2 * lens.min() + 10]))
        span[p] = (downs[part_of == p][0], ups[part_of == p][-1])
    true_dah = np.array([(ups[i] - downs[i]) * 1000 > 2 * ditms[part_of[i]] for i in range(len(rows))])

    pitch = float(o["tone_hz"])
    z = E.mixed_frames(x, sr, pitch); T = len(z); t = np.arange(T) * E.HOP - zero
    key = np.zeros(T, bool)
    for d0, u0 in zip(downs * scale, ups * scale): key[int((zero + d0) / E.HOP):int((zero + u0) / E.HOP)] = True
    C = np.concatenate([[0j], np.cumsum(z)])
    ea = ((zero + downs * scale) / E.HOP).astype(int); eb = ((zero + ups * scale) / E.HOP).astype(int)
    elem_amp = np.abs(C[eb] - C[ea]) / (eb - ea); mid = (ea + eb) / 2
    amp = np.empty(T); noise = np.empty(T)
    for c in range(0, T, 200):
        lo, hi = max(0, c - 1000), min(T, c + 1000); g = ~key[lo:hi]
        noise[c:c + 200] = np.mean(np.abs(z[lo:hi][g]) ** 2) if g.sum() > 50 else np.mean(np.abs(z) ** 2)
        near = np.abs(mid - c) < 1000
        amp[c:c + 200] = np.median(elem_amp[near]) if near.sum() > 5 else np.median(elem_amp)
    amp = np.sqrt(np.maximum(amp ** 2 - noise / np.median(eb - ea), 1e-9))
    unit = np.full(T, ditms[parts[-1]])
    for p in reversed(parts): unit[t < span[p][1] + 2] = ditms[p]

    # only the first 60 s of sending, to keep it quick
    s1 = int((zero + downs[0] + 60) / E.HOP)
    text, segs = E.decode(z[:s1], unit[:s1] / 1000 / E.HOP, amp[:s1], noise[:s1])
    marks = [(a, b, n == 'dah') for n, a, b in segs if n in ('dit', 'dah')]
    inside = ea < s1
    found = np.zeros(inside.sum(), bool)
    false_marks = wrong_kind = 0; false_len = []
    for a, b, isdah in marks:
        ov = np.flatnonzero((ea[inside] < b) & (eb[inside] > a))
        if len(ov) == 0:
            false_marks += 1; false_len.append(b - a); continue
        i = ov[0]; found[i] = True
        if isdah != true_dah[inside][i]: wrong_kind += 1
    merged = sum(1 for a, b, _ in marks if len(np.flatnonzero((ea[inside] < b) & (eb[inside] > a))) > 1)
    print("first 60 s: %d real elements, %d marks found" % (inside.sum(), len(marks)))
    print("  missed %d   false (in a gap) %d   wrong kind %d   one mark over two elements %d" % ((~found).sum(), false_marks, wrong_kind, merged))
    if false_len: print("  false mark lengths (frames):", sorted(false_len)[:40])
    print("  text:", text)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
