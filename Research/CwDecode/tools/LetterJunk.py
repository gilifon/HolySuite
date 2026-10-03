"""
WHICH OF THE NEW DECODER'S LETTERS ARE REAL AND WHICH ARE JUNK - and what tells them apart.

ElementBench.exe with LETTER_LOG=<file> writes every letter it shows: recording, dits and dahs,
evidence, whether a letter of the same word was already on screen, whether the plain decoder heard a
station there, and where it ended. This lines those letters up with the text that was sent (difflib,
letter by letter, per recording): a letter inside a matching run is REAL, any other is JUNK. Then it
prints how the two differ on each of those things, and what a stricter rule would keep and drop.

  LetterJunk.py <letters.tsv> <SENT.txt> [more tsv/SENT pairs...]
"""

import difflib
import re
import sys
from collections import defaultdict

MORSE = {
    '.-': 'A', '-...': 'B', '-.-.': 'C', '-..': 'D', '.': 'E', '..-.': 'F', '--.': 'G', '....': 'H', '..': 'I',
    '.---': 'J', '-.-': 'K', '.-..': 'L', '--': 'M', '-.': 'N', '---': 'O', '.--.': 'P', '--.-': 'Q', '.-.': 'R',
    '...': 'S', '-': 'T', '..-': 'U', '...-': 'V', '.--': 'W', '-..-': 'X', '-.--': 'Y', '--..': 'Z',
    '-----': '0', '.----': '1', '..---': '2', '...--': '3', '....-': '4', '.....': '5', '-....': '6',
    '--...': '7', '---..': '8', '----.': '9', '-..-.': '/', '-...-': '=', '..--..': '?', '.-.-.-': '.',
    '--..--': ',',
}


def load(tsv, sent):
    ref = re.sub(r"[^A-Z0-9/=?.,]", "", open(sent).read().upper())
    per = defaultdict(list)
    for line in open(tsv, encoding='utf-8-sig'):
        name, pat, ev, inword, plain, end = line.rstrip('\n').split('\t')
        per[name].append(dict(pat=pat, ev=float(ev), inword=inword == '1', plain=plain == '1',
                              letter=MORSE.get(pat, '#')))
    rows = []
    for name, letters in per.items():
        said = ''.join(l['letter'] for l in letters)
        good = set()
        for bl in difflib.SequenceMatcher(None, ref, said, autojunk=False).get_matching_blocks():
            if bl.size >= 2:                          # a lone match proves nothing
                good.update(range(bl.b, bl.b + bl.size))
        for i, l in enumerate(letters):
            l['real'] = i in good
            rows.append(l)
    return rows


def main(args):
    rows = []
    for i in range(0, len(args), 2):
        rows += load(args[i], args[i + 1])
    real = [r for r in rows if r['real']]; junk = [r for r in rows if not r['real']]
    print("%d letters shown: %d real, %d junk" % (len(rows), len(real), len(junk)))

    def share(xs, f):
        return 100.0 * sum(1 for x in xs if f(x)) / max(1, len(xs))
    print("single-element letters (E, T):  real %4.1f%%   junk %4.1f%%" % (share(real, lambda r: len(r['pat']) == 1), share(junk, lambda r: len(r['pat']) == 1)))
    print("first letter of a word:         real %4.1f%%   junk %4.1f%%" % (share(real, lambda r: not r['inword']), share(junk, lambda r: not r['inword'])))
    for q in (10, 25, 50, 75):
        def pct(xs):
            v = sorted(x['ev'] for x in xs)
            return v[int(len(v) * q / 100)] if v else 0
        print("evidence %2d%%:  real %5.2f   junk %5.2f" % (q, pct(real), pct(junk)))

    print("\nif a letter of length n needs evidence >= g (in-word letters too):")
    for g in (0.3, 0.6, 1.0, 1.5):
        for n in (1, 2):
            keep_r = sum(1 for r in real if len(r['pat']) > n or r['ev'] >= g)
            keep_j = sum(1 for r in junk if len(r['pat']) > n or r['ev'] >= g)
            print("  length <= %d needs %.1f:  keeps %d of %d real, %d of %d junk" % (n, g, keep_r, len(real), keep_j, len(junk)))


if __name__ == '__main__':
    main(sys.argv[1:])
