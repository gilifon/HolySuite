# WHAT KIND OF MISTAKE IS IT? Not how many words are wrong - what is WRONG with them.
#
# A word is lined up with the word W1AW actually sent, and both are turned back into dots and
# dashes. Then the fault names itself:
#
#   the same dots and dashes, different letters  -> the ELEMENTS were all heard; the GAPS were read
#                                                   wrong (a letter split in two, or two run together)
#   one dot heard as a dash, or the other way    -> a length was judged wrong
#   one element missing / one element too many   -> the key threshold missed it or invented it
#
# That tells us which of the three to work on, instead of guessing.
#
#   WhyWrong.py ARLP037.txt decoded.txt

import sys, re, difflib
from collections import Counter

MORSE = {
 'A':'.-','B':'-...','C':'-.-.','D':'-..','E':'.','F':'..-.','G':'--.','H':'....','I':'..',
 'J':'.---','K':'-.-','L':'.-..','M':'--','N':'-.','O':'---','P':'.--.','Q':'--.-','R':'.-.',
 'S':'...','T':'-','U':'..-','V':'...-','W':'.--','X':'-..-','Y':'-.--','Z':'--..',
 '0':'-----','1':'.----','2':'..---','3':'...--','4':'....-','5':'.....','6':'-....',
 '7':'--...','8':'---..','9':'----.','/':'-..-.','=':'-...-','?':'..--..','.':'.-.-.-',
 ',':'--..--',':':'---...','-':'-....-',"'":'.----.','(':'-.--.',')':'-.--.-','+':'.-.-.',
}

def words(text):
    text = text.upper()
    text = re.sub(r'<[^>]*>', ' ', text)          # prosigns the decoder writes as <AS>
    return [w for w in text.split() if w]

def morse(word):
    out = []
    for ch in word:
        if ch not in MORSE: return None
        out.append(MORSE[ch])
    return ' '.join(out)

def stream(word):
    m = morse(word)
    return None if m is None else m.replace(' ', '')

def one_edit(a, b):
    """'sub', 'del', 'ins' or None - how b differs from a by a single element."""
    if a is None or b is None: return None
    if len(a) == len(b):
        d = sum(1 for x, y in zip(a, b) if x != y)
        return 'sub' if d == 1 else None
    if len(a) == len(b) + 1:
        for i in range(len(a)):
            if a[:i] + a[i+1:] == b: return 'del'
        return None
    if len(b) == len(a) + 1:
        for i in range(len(b)):
            if b[:i] + b[i+1:] == a: return 'ins'
        return None
    return None

ref = words(open(sys.argv[1], encoding='utf-8', errors='replace').read())

got = []
for line in open(sys.argv[2], encoding='utf-8', errors='replace'):
    if '\t' in line: line = line.split('\t', 1)[1]
    got += words(line)

sm = difflib.SequenceMatcher(a=ref, b=got, autojunk=False)
kinds = Counter()
examples = {}

for tag, i1, i2, j1, j2 in sm.get_opcodes():
    if tag == 'equal':
        kinds['right'] += i2 - i1
    elif tag == 'replace' and (i2 - i1) == (j2 - j1):
        for r, g in zip(ref[i1:i2], got[j1:j2]):
            sr, sg = stream(r), stream(g)
            if sr is not None and sr == sg: kind = 'gaps read wrong'
            else:
                e = one_edit(sr, sg)
                kind = {'sub': 'one dot/dash misjudged',
                        'del': 'one element missed',
                        'ins': 'one element invented'}.get(e, 'more than one element wrong')
            kinds[kind] += 1
            examples.setdefault(kind, []).append(r + ' -> ' + g)
    else:
        n = max(i2 - i1, j2 - j1)
        kinds['words split, joined or lost'] += n
        examples.setdefault('words split, joined or lost', []).append(
            ' '.join(ref[i1:i2])[:40] + ' -> ' + ' '.join(got[j1:j2])[:40])

total = sum(kinds.values())
print('%d words lined up against the bulletin' % total)
for kind, n in kinds.most_common():
    print('  %-30s %6d  %5.1f%%' % (kind, n, n * 100.0 / total))
    for e in examples.get(kind, [])[:6]:
        print('        %s' % e)
