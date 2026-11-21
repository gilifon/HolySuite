"""
HYBRID REDESIGNED, MEASURED BEFORE IT IS BUILT (2026-10-27). Hybrid patched Plain's letters one by one with the
network's, and lost words the network had read (letters lost at word ends, wrong spaces, words glued). The
design tried here: once the network has read a stretch, the text there IS the network's reading - its letters
and its word gaps - with one filter kept from Hybrid: a network letter stays only if Plain heard something
within NEAR seconds of it (on an empty band the network invents letters, Plain hears nothing).

Reads MixDump2's letter streams (build/md2/<set>__<name>.tsv) and writes "name<TAB>text" files for ScoreText.py.

  HybridSim.py <near seconds> <set> <out.txt> [names filter]
"""
import glob
import os
import sys

import numpy as np


def load(path):
    plain, net, gaps = [], [], []
    for line in open(path, encoding='utf-8'):
        k, a, b = line.rstrip('\n').split('\t')
        if k == 'P':
            plain.append(float(b))
        elif k == 'N':
            net.append((a, float(b)))
        elif k == 'G':
            gaps.append(float(b))
    return np.array(sorted(plain)), net, sorted(gaps)


def text(path, near):
    plain, net, gaps = load(path)
    out = []
    gi = 0
    last_t = None
    for letter, t in net:
        # the network's word gaps that fall before this letter
        gap = False
        while gi < len(gaps) and gaps[gi] < t:
            gap = True
            gi += 1
        if near is not None and near >= 0:
            if len(plain) == 0:
                continue
            i = np.searchsorted(plain, t)
            d = min(abs(plain[j] - t) for j in (i - 1, i) if 0 <= j < len(plain))
            if d > near:
                continue
        if gap and out and out[-1] != ' ':
            out.append(' ')
        out.append(letter)
        last_t = t
    s = ''.join(out)
    return s.replace('<BT>', '=').replace('<AR>', '+')


def main():
    near = float(sys.argv[1])
    near = None if near < 0 else near
    setname, outp = sys.argv[2], sys.argv[3]
    only = sys.argv[4] if len(sys.argv) > 4 else None
    files = sorted(glob.glob(os.path.join(os.path.dirname(__file__), '..', 'build', 'md2', setname + '__*.tsv')))
    with open(outp, 'w', encoding='utf-8') as f:
        for p in files:
            name = os.path.basename(p)[len(setname) + 2:-4]
            if only and not any(o.lower() in name.lower() for o in only.split(',')):
                continue
            f.write(name + '\t' + text(p, near) + '\n')


if __name__ == '__main__':
    main()
