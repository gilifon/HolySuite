"""
The waterfall checks (steadiness, beside, gap fill - see CwElementDecoder.MeasureLetter) on real and
junk letters: how each separates them, and what a threshold would keep of each.

  LetterJunk2.py      (reads build/letters_<session>.tsv for his four air sessions)
"""

import difflib
import re
import sys

sys.path.insert(0, __import__('os').path.dirname(__import__('os').path.abspath(__file__)))
from LetterJunk import MORSE


def load(tsv, sent):
    ref = re.sub(r"[^A-Z0-9/=?.,]", "", open(sent).read().upper())
    per = {}
    for line in open(tsv, encoding='utf-8-sig'):
        f = line.rstrip('\n').split('\t')
        if len(f) < 9: continue
        per.setdefault(f[0], []).append(dict(pat=f[1], ev=float(f[2]), steady=float(f[6]), beside=float(f[7]),
                                              fill=float(f[8]), letter=MORSE.get(f[1], '#')))
    rows = []
    for name, letters in per.items():
        said = ''.join(l['letter'] for l in letters)
        good = set()
        for bl in difflib.SequenceMatcher(None, ref, said, autojunk=False).get_matching_blocks():
            if bl.size >= 2: good.update(range(bl.b, bl.b + bl.size))
        for i, l in enumerate(letters):
            l['real'] = i in good
            rows.append(l)
    return rows


rows = []
for d in ('4z5sl', '4z5sl2', '4z5sl3', 'fist1'):
    rows += load('build/letters_%s.tsv' % d, 'recordings/%s/SENT.txt' % d)
real = [r for r in rows if r['real']]; junk = [r for r in rows if not r['real']]
print("%d letters shown: %d real, %d junk" % (len(rows), len(real), len(junk)))
for key in ('steady', 'beside', 'fill'):
    def pct(xs, q):
        v = sorted(x[key] for x in xs); return v[min(len(v) - 1, int(len(v) * q / 100))]
    print("%-7s  real p10 %6.2f p50 %6.2f p90 %6.2f  |  junk p10 %6.2f p50 %6.2f p90 %6.2f"
          % (key, pct(real, 10), pct(real, 50), pct(real, 90), pct(junk, 10), pct(junk, 50), pct(junk, 90)))
for key, ths, above in (('steady', (0.1, 0.2, 0.3, 0.4), True), ('beside', (1.5, 2, 3, 4), True), ('fill', (0.5, 0.3, 0.2, 0.15), False)):
    for th in ths:
        keep = (lambda r: r[key] >= th) if above else (lambda r: r[key] <= th)
        kr = sum(1 for r in real if keep(r)); kj = sum(1 for r in junk if keep(r))
        print("  %s %s %.2f: keeps %.0f%% of real, %.0f%% of junk" % (key, '>=' if above else '<=', th, 100.0 * kr / len(real), 100.0 * kj / len(junk)))
