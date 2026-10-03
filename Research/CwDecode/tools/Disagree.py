"""WHERE THE TWO READERS DIFFER - the letters only one of them printed, from a replay.

He asked for it: leave the radio on a CW station, and find by yourself every letter that only Plain or
only New decoded, instead of him having to catch it on the screen. Reads the <name>_letters.tsv that
ReplayStrip.py / ReplayLetters.exe writes (both readers' letters, each at the middle of its own marks),
pairs the two lines letter by letter by position, and lists every place they do not agree:
    only Plain   - Plain printed a letter there, New printed nothing
    only New     - the other way
    different    - both printed, but not the same letter
with the second it happened, so the replay picture of that moment can be looked at.

    Disagree.py <letters.tsv> [rate, default 8000]
"""
import csv
import sys

PAIR_SECONDS = 0.12        # two letters this close are the same marks read twice


def main():
    path = sys.argv[1]
    rate = int(sys.argv[2]) if len(sys.argv) > 2 else 8000
    plain, new = [], []
    for r in csv.DictReader(open(path), delimiter='\t', quoting=csv.QUOTE_NONE):
        (plain if r['reader'] == 'Plain' else new).append((int(r['sample']) / rate, r['letter']))
    plain.sort()
    new.sort()

    # nearest pairs first, each letter used once
    pairs = []
    for i, (tp, _) in enumerate(plain):
        for j, (tn, _) in enumerate(new):
            if abs(tp - tn) <= PAIR_SECONDS:
                pairs.append((abs(tp - tn), i, j))
    pairs.sort()
    used_p, used_n, matched = set(), set(), []
    for _, i, j in pairs:
        if i in used_p or j in used_n:
            continue
        used_p.add(i)
        used_n.add(j)
        matched.append((i, j))

    rows = []
    for i, j in matched:
        if plain[i][1] != new[j][1]:
            rows.append((plain[i][0], 'different', plain[i][1], new[j][1]))
    for i, (t, l) in enumerate(plain):
        if i not in used_p:
            rows.append((t, 'only Plain', l, ''))
    for j, (t, l) in enumerate(new):
        if j not in used_n:
            rows.append((t, 'only New', '', l))
    rows.sort()

    same = len(matched) - sum(1 for r in rows if r[1] == 'different')
    print('same %d   different %d   only Plain %d   only New %d' % (
        same, sum(1 for r in rows if r[1] == 'different'),
        sum(1 for r in rows if r[1] == 'only Plain'), sum(1 for r in rows if r[1] == 'only New')))
    for t, kind, p, n in rows:
        print('%8.2f s  %-10s  Plain %-6s  New %-6s' % (t, kind, p, n))


if __name__ == '__main__':
    main()
