"""
A BIGGER WORD LIST FOR THE NETWORK'S READING (2026-10-27). beam.py rewards known words; its English came from
Python's documentation (3390 words), which misses everyday words - SEPTEMBER, SOLAR, REPORT, QUICK, BROWN were
all "unknown" on the test texts. Hunspell's en_US dictionary (SCOWL, a permissive licence - it may ship) has
~49,000 stems; with its affix rules (plurals, -ED, -ING, -LY ...) applied this writes every word, upper case,
letters only, MINLEN..11 long, one a line.

  HunspellWords.py <en_US.dic> <en_US.aff> <out.txt> [minlen=2]
"""
import re
import sys


def rules(aff_path):
    """flag -> (kind 'PFX'/'SFX', cross, [(strip, add, condition regex)])"""
    out = {}
    for line in open(aff_path, encoding='latin-1'):
        p = line.split()
        if len(p) < 4 or p[0] not in ('PFX', 'SFX'):
            continue
        kind, flag = p[0], p[1]
        if flag not in out:
            out[flag] = (kind, p[2] == 'Y', [])      # header: PFX A Y 1
            continue
        strip = '' if p[2] == '0' else p[2]
        add = '' if p[3] == '0' else p[3].split('/')[0]
        cond = p[4] if len(p) > 4 else '.'
        rx = re.compile(('^' + cond) if kind == 'PFX' else (cond + '$'))
        out[flag][2].append((strip, add, rx))
    return out


def expand(stem, flags, rl):
    words = {stem}
    for f in flags:
        if f not in rl:
            continue
        kind, _, entries = rl[f]
        for strip, add, rx in entries:
            if not rx.search(stem):
                continue
            if kind == 'SFX':
                if strip and not stem.endswith(strip):
                    continue
                words.add(stem[:len(stem) - len(strip)] + add)
            else:
                if strip and not stem.startswith(strip):
                    continue
                words.add(add + stem[len(strip):])
    return words


def main():
    dic, aff, outp = sys.argv[1], sys.argv[2], sys.argv[3]
    minlen = int(sys.argv[4]) if len(sys.argv) > 4 else 2
    rl = rules(aff)
    got = set()
    lines = open(dic, encoding='latin-1').read().splitlines()[1:]
    for line in lines:
        stem, _, flags = line.strip().partition('/')
        for w in expand(stem, flags, rl):
            w = w.upper()
            if re.fullmatch(r'[A-Z]+', w) and minlen <= len(w) <= 11:
                got.add(w)
    with open(outp, 'w', encoding='utf-8') as f:
        for w in sorted(got):
            f.write(w + '\n')
    print(len(got), 'words')


if __name__ == '__main__':
    main()
