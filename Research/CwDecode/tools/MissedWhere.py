# WHERE DOES THE MISSING ELEMENT SIT?
#
# WhyWrong.py says the commonest fault is a word wrong by exactly one element. This finds, for every
# one of those, WHICH element of the sent word went astray: was it a dit or a dah, was it the first
# of its letter, one in the middle, or the last one, and what came just before it. If missed
# elements pile up in one place - the last dit of a letter, say, where the silence after it is three
# times as long - then there is a narrow rule worth writing. If they are spread evenly, there is not.
#
#   MissedWhere.py ARLP037.txt decoded.txt

import sys, re, difflib
from collections import Counter

MORSE = {
 'A':'.-','B':'-...','C':'-.-.','D':'-..','E':'.','F':'..-.','G':'--.','H':'....','I':'..',
 'J':'.---','K':'-.-','L':'.-..','M':'--','N':'-.','O':'---','P':'.--.','Q':'--.-','R':'.-.',
 'S':'...','T':'-','U':'..-','V':'...-','W':'.--','X':'-..-','Y':'-.--','Z':'--..',
 '0':'-----','1':'.----','2':'..---','3':'...--','4':'....-','5':'.....','6':'-....',
 '7':'--...','8':'---..','9':'----.','/':'-..-.','=':'-...-',
}

def words(text):
    text = re.sub(r'<[^>]*>', ' ', text.upper())
    return [w for w in text.split() if w]

def elements(word):
    """[(symbol, letter, index in letter, length of letter)] or None if a character is unknown."""
    out = []
    for ch in word:
        if ch not in MORSE: return None
        code = MORSE[ch]
        for i, sym in enumerate(code):
            out.append((sym, ch, i, len(code)))
    return out

def place(i, n):
    return 'only one' if n == 1 else 'first' if i == 0 else 'last' if i == n - 1 else 'inside'

def stream(word):
    e = elements(word)
    return None if e is None else ''.join(s for s, _, _, _ in e)

def deletion_index(sent, heard):
    """Index in `sent` of the one element missing from `heard`, or None."""
    if len(sent) != len(heard) + 1: return None
    hits = [i for i in range(len(sent)) if sent[:i] + sent[i+1:] == heard]
    if not hits: return None
    return hits[0]              # a run of equal symbols gives the same answer whichever is blamed

def substitution_index(sent, heard):
    if len(sent) != len(heard): return None
    d = [i for i in range(len(sent)) if sent[i] != heard[i]]
    return d[0] if len(d) == 1 else None

ref = words(open(sys.argv[1], encoding='utf-8', errors='replace').read())

# ONE RECORDING AT A TIME. Every receiver heard the same bulletin, so throwing all their text into
# one list and lining that up against the reference matches the first pass and calls the other six
# receivers gibberish - which is how this script first reported a single missed element in twenty
# thousand words.
heard = []
for line in open(sys.argv[2], encoding='utf-8', errors='replace'):
    if '\t' in line: line = line.split('\t', 1)[1]
    ws = words(line)
    if ws: heard.append(ws)

missed = Counter(); misjudged = Counter(); letters = Counter()
sent_all = Counter()
examples = []

for got in heard:
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(a=ref, b=got, autojunk=False).get_opcodes():
        if tag == 'equal':
            for w in ref[i1:i2]:
                e = elements(w)
                if e:
                    for sym, ch, i, n in e: sent_all[(sym, place(i, n))] += 1
        if tag != 'replace' or (i2 - i1) != (j2 - j1): continue
        for r, g in zip(ref[i1:i2], got[j1:j2]):
            sr, sg = stream(r), stream(g)
            if sr is None or sg is None: continue
            e = elements(r)
            d = deletion_index(sr, sg)
            if d is not None:
                sym, ch, i, n = e[d]
                where = place(i, n)
                before = 'nothing - letter starts' if i == 0 else e[d-1][0]
                missed[(sym, where, before)] += 1
                letters[ch] += 1
                if len(examples) < 12: examples.append('%s -> %s   lost %s (%s, after %s)' % (r, g, sym, where, before))
                continue
            s = substitution_index(sr, sg)
            if s is not None:
                sym, ch, i, n = e[s]
                where = place(i, n)
                misjudged[(sym, where)] += 1

print('MISSED ELEMENTS (%d)' % sum(missed.values()))
for (sym, where, before), n in missed.most_common():
    print('  %s  %-16s after %-22s %4d' % ('dit' if sym == '.' else 'dah', where, before, n))
print()
print('  letters hurt: %s' % ', '.join('%s x%d' % (c, n) for c, n in letters.most_common(12)))
print()
for e in examples: print('  ' + e)
print()
print('FOR COMPARISON - every element in the words that came out RIGHT (%d)' % sum(sent_all.values()))
for (sym, where), n in sent_all.most_common():
    print('  %s  %-10s %6d  %5.1f%%' % ('dit' if sym == '.' else 'dah', where, n, n * 100.0 / sum(sent_all.values())))
print()
print('MISJUDGED (dit read as dah or the other way) (%d)' % sum(misjudged.values()))
for (sym, where), n in misjudged.most_common():
    print('  sent %s  %-16s %4d' % ('dit' if sym == '.' else 'dah', where, n))
