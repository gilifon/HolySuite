"""
WHERE HYBRID LOSES WORDS THE NETWORK HAD (2026-10-27). For each recording: the sent words, the network-alone
text and the Hybrid text (HybridText.exe's _net.txt / _hybrid.txt) are aligned word by word (difflib, as
ScoreText.py does); every sent word the network read right and Hybrid did not is listed with what Hybrid
showed in its place, and the losses are sorted into kinds.

  HybridLosses.py <SENT.txt> <prefix>   (reads <prefix>_net.txt and <prefix>_hybrid.txt)
"""
import collections
import difflib
import re
import sys


def words(text):
    return re.findall(r"[A-Z0-9]+", text.upper())


def matched(ref, said):
    """-> {ref index: said index} for the words read right."""
    m = {}
    for bl in difflib.SequenceMatcher(None, ref, said, autojunk=False).get_matching_blocks():
        for k in range(bl.size):
            m[bl.a + k] = bl.b + k
    return m


def region(ref_i, m, said):
    """What `said` has between the nearest matched words before and after ref word ref_i."""
    before = [b for a, b in m.items() if a < ref_i]
    after = [b for a, b in m.items() if a > ref_i]
    lo = max(before) + 1 if before else 0
    hi = min(after) if after else len(said)
    return said[lo:hi]


def kind(ref_word, shown):
    joined = ''.join(shown)
    if not shown:
        return 'missing'
    if joined == ref_word:
        return 'split'                       # the right letters, a space too many
    if any(ref_word in w and w != ref_word for w in shown):
        return 'glued'                       # the right letters, glued to the next or the one before
    if len(shown) == 1 and len(shown[0]) == len(ref_word):
        return 'letter wrong'
    if len(shown) == 1 and abs(len(shown[0]) - len(ref_word)) <= 2:
        return 'letter missing or extra'
    return 'other'


def main(sent_file, prefix):
    ref = words(open(sent_file, encoding='utf-8').read())
    kinds = collections.Counter()
    examples = collections.defaultdict(list)
    lost = gained = 0
    for net_line, hyb_line in zip(open(prefix + '_net.txt', encoding='utf-8'), open(prefix + '_hybrid.txt', encoding='utf-8')):
        name, net_text = net_line.rstrip('\n').split('\t', 1)
        _, hyb_text = hyb_line.rstrip('\n').split('\t', 1)
        net, hyb = words(net_text), words(hyb_text)
        mn, mh = matched(ref, net), matched(ref, hyb)
        span = range(min(mn) if mn else 0, max(mn) + 1 if mn else 0)
        for i in span:
            if i in mn and i not in mh:
                lost += 1
                shown = region(i, mh, hyb)
                k = kind(ref[i], shown)
                kinds[k] += 1
                if len(examples[k]) < 12:
                    examples[k].append('%s -> %s' % (ref[i], ' '.join(shown[:4]) or '(nothing)'))
            elif i in mh and i not in mn:
                gained += 1
    print('words the network read and Hybrid did not: %d;  the other way round: %d' % (lost, gained))
    for k, n in kinds.most_common():
        print('  %-24s %4d   e.g. %s' % (k, n, ';  '.join(examples[k][:8])))


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
