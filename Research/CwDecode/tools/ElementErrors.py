# WHICH PIECE OF THE MORSE GOES WRONG - counted on the dits, dahs and gaps themselves.
#
# Counting whole words is too coarse when most words come out broken: "THE QUICK BROWN FOX" read as
# "THE UICK BI ANBEE X" is three wrong words, and says nothing about why. So both the text that was
# sent and the text the decoder printed are turned back into what went through the air -
#
#     .  a dit        -  a dah        |  the gap between two letters        #  the gap between words
#
# (the gap inside a letter is implied) - and the two streams are lined up symbol by symbol. What is
# left over names the fault exactly: a dit that was never heard, a letter gap that was not there
# (a letter cut in two), a letter gap that was missed (two letters run together), and so on.
#
#   ElementErrors.py SENT.txt decoded.txt        (decoded.txt: "name<TAB>text" per recording)

import sys, re, difflib
from collections import Counter

MORSE = {
 'A':'.-','B':'-...','C':'-.-.','D':'-..','E':'.','F':'..-.','G':'--.','H':'....','I':'..',
 'J':'.---','K':'-.-','L':'.-..','M':'--','N':'-.','O':'---','P':'.--.','Q':'--.-','R':'.-.',
 'S':'...','T':'-','U':'..-','V':'...-','W':'.--','X':'-..-','Y':'-.--','Z':'--..',
 '0':'-----','1':'.----','2':'..---','3':'...--','4':'....-','5':'.....','6':'-....',
 '7':'--...','8':'---..','9':'----.','/':'-..-.','=':'-...-','?':'..--..','.':'.-.-.-',
 ',':'--..--','-':'-....-','+':'.-.-.',
}
PROSIGN = {'AS':'.-...','AR':'.-.-.','SK':'...-.-','KN':'-.--.','BT':'-...-'}

NAMES = {'.': 'dit', '-': 'dah', '|': 'letter gap', '#': 'word gap'}


def stream(text):
    out = []
    text = text.upper()
    for word in text.split():
        pieces = []
        for m in re.finditer(r'<([A-Z]{2})>|(.)', word):
            if m.group(1):
                code = PROSIGN.get(m.group(1))
            else:
                code = MORSE.get(m.group(2))
            if code: pieces.append(code)
        if not pieces: continue
        if out: out.append('#')
        out.append('|'.join(pieces))
    return ''.join(out)


# LINED UP IN TWO STEPS, NOT ONE. Lining up two long streams of only four different symbols goes
# wrong - there are too many equally good ways to pair dots with dots - and the first version of
# this script reported thousands of faults that were only a poor alignment. So the TEXT is lined up
# first, letter by letter, where forty different characters make the pairing sure; only the short
# stretches that differ are then turned into dots, dashes and gaps and compared.

def letters(text):
    """Upper case, one space between words, a prosign as one placeholder character."""
    text = re.sub(r'<([A-Z]{2})>', lambda m: {'AS': '&', 'AR': '+', 'SK': '$', 'KN': '(', 'BT': '='}.get(m.group(1), ''), text.upper())
    text = ''.join(c for c in text if c == ' ' or c in MORSE or c in '&$(')
    return ' '.join(text.split())

EXTRA = {'&': '.-...', '$': '...-.-', '(': '-.--.'}

def elements(fragment):
    out = []
    for i, c in enumerate(fragment):
        if c == ' ':
            out.append('#')
            continue
        if i > 0 and fragment[i - 1] != ' ': out.append('|')
        out.append(MORSE.get(c) or EXTRA.get(c, ''))
    return ''.join(out)

# A stretch of sent text longer than this with NOTHING printed against it was not misread - it was
# not decoded at all (the signal faded, or the decoder had not yet decided it was Morse). That is a
# different fault from a wrong dit, and it is counted separately.
UNHEARD = 8

# Which paragraph of the sent file a character belongs to - each paragraph went out as one part, at
# its own speed, so faults per paragraph are faults per speed.
PART_STARTS = []

def part_of(i):
    n = 0
    for k, start in enumerate(PART_STARTS):
        if i >= start: n = k
    return n

by_part = Counter()
unheard_by_part = Counter()

def score(sent, heard, faults, unheard):
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(a=sent, b=heard, autojunk=False).get_opcodes():
        if tag == 'equal': continue
        a, b = sent[i1:i2], heard[j1:j2]
        if tag == 'delete' and len(a) > UNHEARD:
            unheard.append(len(a))
            unheard_by_part[part_of(i1)] += len(a)
            continue
        ea, eb = elements(a), elements(b)
        for t, k1, k2, l1, l2 in difflib.SequenceMatcher(a=ea, b=eb, autojunk=False).get_opcodes():
            if t == 'equal': continue
            x, y = ea[k1:k2], eb[l1:l2]
            by_part[part_of(i1)] += max(len(x), len(y))
            if t == 'replace' and len(x) == len(y):
                for p, q in zip(x, y):
                    faults['%s heard as %s' % (NAMES[p], NAMES[q])] += 1
                continue
            for p in x: faults['%s missed' % NAMES[p]] += 1
            for q in y: faults['%s invented' % NAMES[q]] += 1


def trim_to_transmission(sent, heard):
    """The decoded text of a whole recording also holds whatever came before and after. Keep only
    the stretch that lines up with the sent text, so a neighbour's CQ is not blamed on us."""
    blocks = [b for b in difflib.SequenceMatcher(a=sent, b=heard, autojunk=False).get_matching_blocks() if b.size >= 4]
    if not blocks: return ''
    return heard[blocks[0].b: blocks[-1].b + blocks[-1].size]


paragraphs = [letters(p) for p in open(sys.argv[1], encoding='utf-8', errors='replace').read().split('\n') if p.strip()]
sent = ''
for p in paragraphs:
    if sent: sent += ' '
    PART_STARTS.append(len(sent))
    sent += p
per = Counter(elements(sent))

faults = Counter()
unheard = []
recordings = 0
for line in open(sys.argv[2], encoding='utf-8-sig', errors='replace'):
    if '\t' not in line: continue
    recordings += 1
    name, text = line.rstrip('\n').split('\t', 1)
    heard = trim_to_transmission(sent, letters(text))
    before, gone = sum(faults.values()), sum(unheard)
    score(sent, heard, faults, unheard)
    print('%-20s %5d characters printed   %4d element/gap faults   %4d sent characters never printed'
          % (name, len(heard), sum(faults.values()) - before, sum(unheard) - gone))

print()
print('sent, per recording: %d characters - %d dits, %d dahs, %d letter gaps, %d word gaps'
      % (len(sent), per['.'], per['-'], per['|'], per['#']))
print('never printed at all: %d characters in %d stretches' % (sum(unheard), len(unheard)))
print()
if len(paragraphs) > 1:
    print('by part (all recordings together):')
    for k, p in enumerate(paragraphs):
        print('  part %d  %4d characters sent   %4d element/gap faults (%.2f per character)   %4d characters never printed'
              % (k + 1, len(p), by_part[k], by_part[k] / float(len(p) * recordings), unheard_by_part[k]))
    print()
all_faults = sum(faults.values())
for kind, n in faults.most_common():
    print('  %-28s %5d   %5.1f%%' % (kind, n, n * 100.0 / all_faults))
