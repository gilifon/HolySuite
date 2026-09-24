"""
PROTOTYPE: A DECODER THAT JUDGES WHOLE ELEMENTS, NOT 5 ms AT A TIME.

ElementEvidence.py measured on his fading copies that the average loudness over a whole sent element
separates marks from gaps two to three times better than single 5 ms readings do (e.g. 11.6% wrong
against 5.3%). The plain decoder decides every reading on its own against a line, so it throws that
away: one dip inside a dah breaks it, one bump in a gap invents a dit.

This finds instead the most likely sequence of WHOLE marks and gaps, each of a length Morse allows:

    marks:  a dit (about 1 unit) or a dah (about 3)
    gaps:   inside a letter (1), between letters (3), between words (7 or more)

scored by how well each whole stretch matches "tone" or "no tone" (Gaussian on the loudness the plain
decoder already measures - LevelDump) plus how well its length fits (Gaussian in log length). A
dynamic programme over segment ends (a hidden semi-Markov model) finds the best sequence. The unit is
taken from the plain decoder's own marks over the surrounding twenty seconds, so a speed change is
followed. Nothing is guessed from words: only Morse's own timing is used, and a pattern that spells
no letter is dropped exactly as the plain decoder drops it.

It reads the SAME loudness the plain decoder reads, so any gain is the element-level judgement alone.

  ElementDecoder.py levels.txt out.txt [sigma_scale] [gatefile]
"""

import sys

import numpy as np

MORSE = {
    '.-': 'A', '-...': 'B', '-.-.': 'C', '-..': 'D', '.': 'E', '..-.': 'F', '--.': 'G', '....': 'H', '..': 'I',
    '.---': 'J', '-.-': 'K', '.-..': 'L', '--': 'M', '-.': 'N', '---': 'O', '.--.': 'P', '--.-': 'Q', '.-.': 'R',
    '...': 'S', '-': 'T', '..-': 'U', '...-': 'V', '.--': 'W', '-..-': 'X', '-.--': 'Y', '--..': 'Z',
    '-----': '0', '.----': '1', '..---': '2', '...--': '3', '....-': '4', '.....': '5', '-....': '6',
    '--...': '7', '---..': '8', '----.': '9', '-..-.': '/', '-...-': '=', '..--..': '?', '.-.-.-': '.',
    '--..--': ',', '.-.-.': '+', '.-...': '&',
}

# segment kinds: (is_mark, units, name)
KINDS = [(True, 1.0, 'dit'), (True, 3.0, 'dah'), (False, 1.0, 'egap'), (False, 3.0, 'lgap'), (False, 7.0, 'wgap')]
DUR_SD = 0.28          # spread of a length, in log units - a sloppy fist or fading edges
LONGEST_WORD_GAP = 60  # units; any longer silence is several word gaps


def fit_levels(x):
    """Two-cluster fit of the loudness: tone and no tone, their means and a shared spread."""
    lo, hi = np.percentile(x, 25), np.percentile(x, 85)
    m0, m1 = min(lo, 0.2), max(hi, 0.6)
    for _ in range(20):
        mid = (m0 + m1) / 2
        a, b = x[x < mid], x[x >= mid]
        if len(a) < 10 or len(b) < 10:
            break
        m0, m1 = np.mean(a), np.mean(b)
    s = np.sqrt((np.sum((x[x < mid] - m0) ** 2) + np.sum((x[x >= mid] - m1) ** 2)) / len(x))
    return m0, m1, max(s, 0.05)


def local_units(x, window=4000):
    """Frames per unit around each frame: the median of the shorter half of the plain marks nearby."""
    on = x > 0.45
    edges = np.flatnonzero(np.diff(on.astype(int)))
    starts = edges[on[edges + 1]] + 1 if len(edges) else np.array([], int)
    ends = edges[~on[edges + 1]] + 1 if len(edges) else np.array([], int)
    marks = []
    for s in starts:
        e = ends[ends > s]
        if len(e):
            marks.append((s, e[0] - s))
    u = np.full(len(x), 10.0)
    if not marks:
        return u
    pos = np.array([m[0] for m in marks]); dur = np.array([m[1] for m in marks], float)
    for c in range(0, len(x), window // 4):
        near = dur[(pos > c - window) & (pos < c + window)]
        near = near[near >= 3]
        if len(near) >= 8:
            short = np.sort(near)[: max(3, len(near) // 2)]
            val = float(np.median(short))
            u[c: c + window // 4] = min(max(val, 4.0), 40.0)
    return u


def decode(x, sigma_scale=1.0, gate=None):
    T = len(x)
    # ONLY WHERE A STATION IS THERE. The first run had no idea of an empty frequency and read letters
    # out of the noise before he even began. The plain decoder is good at that one question, so its
    # own answer (GateMask: a station stands out) is used: outside it, the loudness is taken as silence.
    if gate is not None and len(gate) >= T:
        # Where the plain decoder has PROVED Morse (2), widened by a second either side so the first
        # and last letters of a stretch are inside it - "a station stands out" (1) alone let it read
        # letters out of the noise before he began, as the plain decoder itself never prints there.
        proved = np.array([c == '2' for c in gate[:T]], dtype=float)
        present = np.convolve(proved, np.ones(401), mode='same') > 0
        if present.sum() > 200:
            m0, m1, s = fit_levels(x[present])
            x = np.where(present, x, m0)
        else:
            m0, m1, s = fit_levels(x)
    else:
        m0, m1, s = fit_levels(x)
    s *= sigma_scale
    # per-frame log likelihood of tone and of no tone, and their running sums
    ll1 = -0.5 * ((x - m1) / s) ** 2
    ll0 = -0.5 * ((x - m0) / s) ** 2
    c1 = np.concatenate([[0.0], np.cumsum(ll1)])
    c0 = np.concatenate([[0.0], np.cumsum(ll0)])
    unit = local_units(x)

    NEG = -1e18
    # best[t, k]: best score of a segmentation ending exactly at frame t with a segment of kind k
    best = np.full((T + 1, len(KINDS)), NEG)
    back = np.zeros((T + 1, len(KINDS), 2), dtype=np.int32)   # (start frame, previous kind)
    best[0, 3] = 0.0                                           # start as if after a letter gap
    allowed_prev = {  # a mark follows any gap; a gap follows a mark
        0: [2, 3, 4], 1: [2, 3, 4], 2: [0, 1], 3: [0, 1], 4: [0, 1],
    }
    for t in range(1, T + 1):
        u = unit[t - 1]
        for k, (is_mark, units, _) in enumerate(KINDS):
            want = units * u
            lo = max(1, int(want * np.exp(-3 * DUR_SD)))
            hi = int(want * np.exp(3 * DUR_SD)) + 1 if k != 4 else int(LONGEST_WORD_GAP * u)
            hi = min(hi, t)
            if hi < lo:
                continue
            d = np.arange(lo, hi + 1)
            starts = t - d
            prev_best = np.max(best[starts][:, allowed_prev[k]], axis=1)
            prev_arg = np.array(allowed_prev[k])[np.argmax(best[starts][:, allowed_prev[k]], axis=1)]
            emit = (c1[t] - c1[starts]) if is_mark else (c0[t] - c0[starts])
            ld = np.log(d / want)
            if k == 4:
                dur = np.where(ld < 0, -0.5 * (ld / DUR_SD) ** 2, 0.0)   # a word gap may be any length above 7
            else:
                dur = -0.5 * (ld / DUR_SD) ** 2
            score = prev_best + emit + dur
            i = int(np.argmax(score))
            if score[i] > best[t, k]:
                best[t, k] = score[i]
                back[t, k] = (starts[i], prev_arg[i])
    # trace back from the best final gap
    k = int(np.argmax(best[T]))
    t = T
    segs = []
    while t > 0:
        s0, pk = back[t, k]
        segs.append(KINDS[k][2])
        t, k = int(s0), int(pk)
    segs.reverse()

    out, letter = [], ''
    for name in segs:
        if name == 'dit':
            letter += '.'
        elif name == 'dah':
            letter += '-'
        elif name in ('lgap', 'wgap'):
            if letter:
                out.append(MORSE.get(letter, ''))
                letter = ''
            if name == 'wgap':
                out.append(' ')
    if letter:
        out.append(MORSE.get(letter, ''))
    return ''.join(out)


def main(levels_file, out_file, sigma_scale=1.0, gate_file=None):
    gates = {}
    if gate_file:
        for line in open(gate_file, encoding='utf-8-sig'):
            n, m = line.rstrip('\n').split('\t', 1)
            gates[n.strip('﻿')] = m
    with open(out_file, 'w', encoding='utf-8') as out:
        for line in open(levels_file, encoding='utf-8-sig'):
            name, body = line.rstrip('\n').split('\t', 1)
            name = name.strip('﻿')
            x = np.array([float(v) for v in body.split()])
            text = decode(x, float(sigma_scale), gates.get(name))
            out.write('%s\t%s\n' % (name, ' '.join(text.split())))
            print('%-22s %s' % (name, ' '.join(text.split())[:110]))
            sys.stdout.flush()


if __name__ == '__main__':
    main(*sys.argv[1:])
