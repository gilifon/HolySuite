"""
WHAT WAS IN THE AUDIO WHERE AN ELEMENT WAS SENT - and could a better detector have found it?

His IC-7610 keys with machine timing, and the text of his transmissions is known. So once a strong
copy has placed each letter (LetterTimes + the same lining-up LabelReal uses), every dit and dah he
sent can be laid out on the timeline to the reading, and put on every other receiver's timeline by
that receiver's offset. Then, for every element sent, the plain decoder's own loudness (LevelDump:
0 = its noise floor, 1 = its peak; it starts a mark at 0.55 and keeps it above 0.35) is read over the
middle of that element, and over the middle of each gap inside a letter.

WHAT IT DECIDES. The plain decoder judges every 5 ms on its own against a line. A detector that
weighs a whole element at once - an HMM / matched-filter decoder - can only win if the AVERAGE over a
sent element stands clear of the average over a gap even where single readings do not. This measures
exactly that, on the copies where letters are lost:

  - how often a sent element's average is under the start line (so the plain decoder can miss it)
  - how well element averages separate marks from gaps, against how well single readings do

  ElementEvidence.py SENT.txt times.txt levels.txt clockname
"""

import sys
from collections import defaultdict

import numpy as np

sys.path.insert(0, __import__('os').path.dirname(__import__('os').path.abspath(__file__)))
from LabelReal import MORSE, align, reference_letters, read_times  # noqa: E402

READ = 40          # samples per 5 ms reading at 8000 Hz


def local_unit(ref, clock, k, reach=8):
    """Samples per Morse unit around letter k, from neighbours the clock placed (parts differ in speed)."""
    ratios = []
    keys = [j for j in sorted(clock) if abs(j - k) <= reach]
    for i, j in zip(keys, keys[1:]):
        if j != i + 1:
            continue
        code = MORSE[ref[j][0]]
        units = sum(1 if s == '.' else 3 for s in code) + len(code) - 1 + (7 if ref[j][1] != ref[i][1] else 3)
        ratios.append((clock[j] - clock[i]) / units)
    return float(np.median(ratios)) if len(ratios) >= 3 else None


def mid(levels, a, b):
    """Mean over the middle 60% of readings a..b."""
    n = b - a
    lo, hi = int(a + 0.2 * n), int(b - 0.2 * n)
    if hi <= lo or lo < 0 or hi > len(levels):
        return None
    return float(np.mean(levels[lo:hi]))


def best_split(marks, gaps):
    """Lowest error rate of any single line separating the two sets."""
    marks, gaps = np.sort(marks), np.sort(gaps)
    best = 1.0
    for t in np.linspace(-0.2, 1.2, 141):
        err = (np.sum(marks < t) + np.sum(gaps >= t)) / float(len(marks) + len(gaps))
        best = min(best, err)
    return best


def main(sent, times_file, levels_file, clock_name):
    ref = reference_letters(sent)
    times = read_times(times_file)
    levels = {}
    for line in open(levels_file, encoding='utf-8'):
        name, body = line.rstrip('\n').split('\t', 1)
        levels[name] = np.array([float(v) for v in body.split()])

    clock = align(ref, times[clock_name])
    print('clock %s places %d of %d letters sent' % (clock_name, len(clock), len(ref)))

    for name in sorted(times):
        heard = align(ref, times[name])
        offs = [heard[k] - clock[k] for k in heard if k in clock]
        if len(offs) < 20:
            print('%-20s too few letters in common' % name)
            continue
        offset = float(np.median(offs))
        lv = levels[name]

        marks, gaps, marks_lost, reading_marks, reading_gaps = [], [], [], [], []
        for k in clock:
            u = local_unit(ref, clock, k)
            if u is None:
                continue
            code = MORSE[ref[k][0]]
            end = clock[k] + offset
            # lay the letter out backwards from where it ended
            t = end
            spans = []
            for n, sym in enumerate(reversed(code)):
                dur = (3 if sym == '-' else 1) * u
                spans.append(('m', t - dur, t))
                t -= dur
                if n < len(code) - 1:
                    spans.append(('g', t - u, t))
                    t -= u
            letter_lost = k not in heard
            for kind, a, b in spans:
                ra, rb = int(a / READ), int(b / READ)
                v = mid(lv, ra, rb)
                if v is None:
                    continue
                if kind == 'm':
                    marks.append(v)
                    reading_marks.extend(lv[ra:rb])
                    if letter_lost:
                        marks_lost.append(v)
                else:
                    gaps.append(v)
                    reading_gaps.extend(lv[ra:rb])

        marks, gaps, marks_lost = np.array(marks), np.array(gaps), np.array(marks_lost)
        if len(marks) < 50:
            continue
        print('%-20s %4d marks: %4.0f%% averaged under the start line, %4.0f%% under the keep line | '
              'in letters it LOST: %4.0f%% under start, %4.0f%% under keep | gaps over keep line %4.0f%% | '
              'best split: single readings %4.1f%% wrong, element averages %4.1f%% wrong'
              % (name, len(marks), 100 * np.mean(marks < 0.55), 100 * np.mean(marks < 0.35),
                 100 * np.mean(marks_lost < 0.55) if len(marks_lost) else 0,
                 100 * np.mean(marks_lost < 0.35) if len(marks_lost) else 0,
                 100 * np.mean(gaps > 0.35),
                 100 * best_split(np.array(reading_marks), np.array(reading_gaps)),
                 100 * best_split(marks, gaps)))


if __name__ == '__main__':
    main(*sys.argv[1:5])
