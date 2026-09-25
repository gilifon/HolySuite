"""
A WHOLE-ELEMENT DECODER THAT LISTENS TO EACH ELEMENT AS ONE TONE.

ElementOracle.py judged elements by adding up 10 ms loudness readings and still read E I S H: judged
10 to 40 ms at a time, tone and gaps overlap on 18-40% of the readings of the fist session. Yet the
same key log says a whole element stands 8-13 dB over its gap. The difference is HOW the element is
added up. A transmitter's tone keeps its phase through a dit or a dah, so adding the raw signal over
the element - phase and all - is a filter exactly as narrow as the element: 17 Hz for a 60 ms dit,
6 Hz for a dah, against 100 Hz for a 10 ms reading. Noise, having no steady phase, does not add up.

So each possible mark from frame s to frame t is scored by the one number that matters for it:

    Y = the sum of the mixed-down signal over the mark
    log p(tone) - log p(no tone) = log I0(2 a |Y| / s2) - L a^2 / s2

(a Rician against an exponential: a tone of amplitude a per frame in complex noise of power s2 per
frame, phase unknown). Gaps score nothing - they are the "no tone" everything is measured against.
A hidden semi-Markov search then finds the best run of whole dits, dahs and gaps of lengths Morse
allows, exactly as ElementOracle.py.

THIS IS STILL AN ORACLE: the speed, the exact pitch, the tone amplitude and the noise power are
taken from the key log. It answers one question - how much of this weak hand-sent CW the idea can
read at all. Taking those four from the audio alone is the next step, and only worth doing if this
reads well.

  ElementCoherent.py <bench folder> [only-this-file]
"""

import csv
import os
import sys
import wave

import numpy as np
from scipy.special import i0e

MORSE = {
    '.-': 'A', '-...': 'B', '-.-.': 'C', '-..': 'D', '.': 'E', '..-.': 'F', '--.': 'G', '....': 'H', '..': 'I',
    '.---': 'J', '-.-': 'K', '.-..': 'L', '--': 'M', '-.': 'N', '---': 'O', '.--.': 'P', '--.-': 'Q', '.-.': 'R',
    '...': 'S', '-': 'T', '..-': 'U', '...-': 'V', '.--': 'W', '-..-': 'X', '-.--': 'Y', '--..': 'Z',
    '-----': '0', '.----': '1', '..---': '2', '...--': '3', '....-': '4', '.....': '5', '-....': '6',
    '--...': '7', '---..': '8', '----.': '9', '-..-.': '/', '-...-': '=', '..--..': '?', '.-.-.-': '.',
    '--..--': ',',
}

HOP = 0.005
KINDS = [(True, 1.0, 'dit'), (True, 3.0, 'dah'), (False, 1.0, 'egap'), (False, 3.0, 'lgap'), (False, 7.0, 'wgap')]
DUR_SD = 0.30
LONGEST_GAP_UNITS = 60
MARK_COST = 0.0     # evidence a mark must bring beyond breaking even - see ElementBlind
NORMALISE = False   # see decode: needed when scores at different speeds are compared
CHUNK = 0      # 0 = one coherent sum over the whole mark; else pieces of this many frames, fading allowed


def mixed_frames(x, sr, pitch):
    """The signal moved down to 0 Hz at the pitch and summed over each 5 ms: one complex value a frame."""
    h = int(round(HOP * sr))
    n = (len(x) // h) * h
    bb = x[:n] * np.exp(-2j * np.pi * pitch * np.arange(n) / sr)
    return bb.reshape(-1, h).sum(axis=1)


def log_i0(v):
    return np.log(i0e(v)) + v


def decode(z, unit_frames, amp, noise):
    """z: complex frames; amp, noise: per-frame tone amplitude and noise power (from the oracle)."""
    T = len(z)
    C = np.concatenate([[0j], np.cumsum(z)])
    if CHUNK:
        # FADING ALLOWED: the tone is judged in short pieces of CHUNK frames, each with a strength of its
        # own (Rayleigh: |Y|^2 exponential, mean c*s2 + c^2*P with tone, c*s2 without), and a mark's
        # evidence is the sum over the pieces inside it. A piece centred on every frame, so each frame
        # carries 1/c of its piece's evidence.
        c = CHUNK
        S = C[c:] - C[:-c]
        S = np.concatenate([np.full(c // 2, S[0]), S, np.full(T - len(S) - c // 2, S[-1])])[:T]
        P = amp ** 2
        e2 = np.abs(S) ** 2
        llr = np.log(c * noise / (c * noise + c * c * P)) + e2 * (1 / (c * noise) - 1 / (c * noise + c * c * P))
        F = np.concatenate([[0.0], np.cumsum(llr / c)])
        decode.F = F
    NEG = -1e18
    best = np.full((T + 1, len(KINDS)), NEG)
    back = np.zeros((T + 1, len(KINDS), 2), dtype=np.int32)
    best[0, 4] = 0.0
    allowed = {0: [2, 3, 4], 1: [2, 3, 4], 2: [0, 1], 3: [0, 1], 4: [0, 1]}
    for t in range(1, T + 1):
        u = unit_frames[t - 1]; a = amp[t - 1]; s2 = noise[t - 1]
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
            if is_mark and CHUNK:
                emit = F[t] - F[starts] - MARK_COST
            elif is_mark:
                Y = np.abs(C[t] - C[starts])
                emit = log_i0(2 * a * Y / s2) - d * a * a / s2
            else:
                emit = 0.0
            ld = np.log(d / want)
            dur = np.where(ld < 0, -0.5 * (ld / DUR_SD) ** 2, 0.0) if k == 4 else -0.5 * (ld / DUR_SD) ** 2
            if NORMALISE:
                # a proper density over the length in frames (log-normal), so that readings at
                # different speeds - different numbers of segments - can be compared by their score
                dur = dur - np.log(d * DUR_SD * 2.5066)
            score = pb + emit + dur
            i = int(np.argmax(score))
            if score[i] > best[t, k]:
                best[t, k] = score[i]; back[t, k] = (starts[i], pa[i])
    decode.score = float(np.max(best[T]))
    k = int(np.argmax(best[T])); t = T; segs = []
    while t > 0:
        s0, pk = back[t, k]
        segs.append((KINDS[k][2], int(s0), t)); t, k = int(s0), int(pk)
    segs.reverse()
    out, letter = [], ''
    for name, _, _ in segs:
        if name == 'dit': letter += '.'
        elif name == 'dah': letter += '-'
        else:
            if name in ('lgap', 'wgap') and letter:
                out.append(MORSE.get(letter, '')); letter = ''
            if name == 'wgap': out.append(' ')
    if letter: out.append(MORSE.get(letter, ''))
    return ' '.join(''.join(out).split()), segs


def main(folder, only=None):
    rows = [r for r in csv.DictReader(open(os.path.join(folder, "edges.tsv")), delimiter="\t") if r["actual_down_ms"] != "nan"]
    offs = list(csv.DictReader(open(os.path.join(folder, "offsets.tsv")), delimiter="\t"))
    parts = sorted({int(r["part"]) for r in rows})
    ditms, span = {}, {}
    for p in parts:
        pr = [r for r in rows if int(r["part"]) == p]
        lens = np.array([float(r["actual_up_ms"]) - float(r["actual_down_ms"]) for r in pr])
        ditms[p] = float(np.median(lens[lens < 2 * np.min(lens) + 10]))
        span[p] = (float(pr[0]["actual_down_ms"]) / 1000, float(pr[-1]["actual_up_ms"]) / 1000)
    downs = np.array([float(r["actual_down_ms"]) / 1000 for r in rows])
    ups = np.array([float(r["actual_up_ms"]) / 1000 for r in rows])

    out = open(os.path.join(folder, "..", "..", "build", "coherent_%s%s.txt" % (os.path.basename(folder), "_" + only if only else "")), "w", encoding="utf-8")
    for o in offs:
        if only and only not in o["file"]:
            continue
        w = wave.open(os.path.join(folder, o["file"])); sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float); w.close()
        zero = float(o["zero_s"])
        # the receiver's clock runs a little apart from the PC's - see CutFist.py
        scale = 1 + float(o.get("drift_ppm") or 0) / 1e6
        fd, fu = downs * scale, ups * scale

        # ORACLE PITCH: the one, to a tenth of a Hz, at which the known elements add up loudest
        def element_power(pitch):
            z = mixed_frames(x, sr, pitch); C = np.concatenate([[0j], np.cumsum(z)])
            a = ((zero + fd) / HOP).astype(int); b = ((zero + fu) / HOP).astype(int)
            ok = (a >= 0) & (b < len(z))
            return np.median(np.abs(C[b[ok]] - C[a[ok]]) ** 2 / (b[ok] - a[ok]))
        p0 = float(o["tone_hz"])
        grid = np.arange(p0 - 8, p0 + 8.01, 0.5)
        p1 = grid[np.argmax([element_power(p) for p in grid])]
        grid = np.arange(p1 - 0.5, p1 + 0.51, 0.1)
        pitch = grid[np.argmax([element_power(p) for p in grid])]

        z = mixed_frames(x, sr, pitch)
        T = len(z); t = np.arange(T) * HOP - zero
        key = np.zeros(T, bool)
        for d0, u0 in zip(fd, fu):
            key[int((zero + d0) / HOP):int((zero + u0) / HOP)] = True

        # ORACLE LEVELS, followed through fades: over +-5 s, the noise power in the gaps and the tone
        # amplitude from the elements' own coherent sums
        C = np.concatenate([[0j], np.cumsum(z)])
        ea = ((zero + fd) / HOP).astype(int); eb = ((zero + fu) / HOP).astype(int)
        elem_amp = np.abs(C[eb] - C[ea]) / (eb - ea)
        elem_mid = (ea + eb) / 2
        amp = np.empty(T); noise = np.empty(T)
        for c in range(0, T, 200):
            lo, hi = max(0, c - 1000), min(T, c + 1000)
            g = ~key[lo:hi]
            noise[c:c + 200] = np.mean(np.abs(z[lo:hi][g]) ** 2) if g.sum() > 50 else np.mean(np.abs(z) ** 2)
            near = np.abs(elem_mid - c) < 1000
            amp[c:c + 200] = np.median(elem_amp[near]) if near.sum() > 5 else np.median(elem_amp)
        # the coherent sum of a tone element still carries its share of noise; take that out
        amp = np.sqrt(np.maximum(amp ** 2 - noise / (np.median(eb - ea)), 1e-9))

        unit = np.full(T, ditms[parts[-1]])
        for p in reversed(parts):
            unit[t < span[p][1] + 2] = ditms[p]
        text, segs = decode(z, unit / 1000 / HOP, amp, noise)
        snr = 10 * np.log10(np.median(amp) ** 2 / np.median(noise))
        out.write("%s\t%s\n" % (o["file"][:-4], text))
        print("%-18s pitch %.1f  tone/noise per 5ms %.1f dB  %s" % (o["file"][:-4], pitch, snr, text[:150]))
        sys.stdout.flush()
    out.close()


if __name__ == "__main__":
    main(*sys.argv[1:])
