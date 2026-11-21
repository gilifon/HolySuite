"""
ARE THE GREY LETTERS REAL? (2026-10-21) - Mix (Hybrid) checked against his on-air sessions, where every letter
sent and the moment it ended are known (FistKeyer's log, aligned per receiver piece).

For each chosen receiver piece: tools/MixDump.exe gives Plain's printed letters, the network's letters and the
stretches it finished, exactly as the decode window gets them; this rebuilds Mix's verdict for every Plain letter
(confirmed, replaced, grey, never checked) the way CwMixText does, and looks each one up in what was sent:
a letter is REAL if the same letter ended within 0.15 s of it.

  mix_truth.py <pieces per receiver> <session dir> [...]
"""
import collections
import json
import os
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import realdata
import finetune_real as fr

CD = os.path.join(HERE, '..')
DUMP = os.path.join(CD, 'build', 'pt', 'MixDump.exe')
NET = os.path.join(CD, 'build', 'CwDeep_centred1.bin')
SAME, NEAR, TRUE_TOL = 0.12, 1.0, 0.15


def run(path):
    out = os.path.join(CD, 'build', 'mixdump', os.path.basename(os.path.dirname(os.path.dirname(path))) + '_' + os.path.basename(path) + '.tsv')
    if not os.path.exists(out):
        os.makedirs(os.path.dirname(out), exist_ok=True)
        subprocess.run([DUMP, NET, path, out], check=True, capture_output=True)
    return out


def mix(lines):
    """-> list of [letter, T, state] for Plain's letters; state confirmed / replaced / grey / unchecked."""
    entries, waiting = [], []
    for kind, a, b in lines:
        if kind == 'P':
            entries.append({'L': a, 'T': float(b), 'state': 'unchecked', 'settled': False, 'doubt': False})
        elif kind == 'N':
            waiting.append((a, float(b)))
        elif kind == 'C':
            lo, hi = float(a), float(b)
            net = sorted([w for w in waiting if w[1] < hi], key=lambda w: w[1])
            waiting = [w for w in waiting if w[1] >= hi]
            for L, t in net:
                best, gap = None, SAME
                for e in entries:
                    g = abs(e['T'] - t)
                    if (not e['settled'] or e['doubt']) and g <= gap:
                        best, gap = e, g
                if best is not None:
                    best['state'] = 'confirmed' if best['L'] == L else 'replaced'
                    best['net'] = L
                    best['settled'], best['doubt'] = True, False
            for e in entries:
                if not e['settled'] and lo <= e['T'] < hi:
                    e['state'], e['settled'], e['doubt'] = 'grey', True, True
    return entries


def main():
    per = int(sys.argv[1])
    jobs = []
    truth = {}
    for d in sys.argv[2:]:
        edges, texts = realdata.read_edges(d)
        lets = fr.letters(edges)
        rows = json.load(open(os.path.join(d, 'align.json')))
        by = collections.defaultdict(list)
        for r in rows:
            if r['score'] >= fr.MIN_CORR:
                by[os.path.basename(r['path']).split('_')[0]].append(r)
        for rx, rs in by.items():
            rs = sorted(rs, key=lambda r: -r['score'])[:per]
            for r in rs:
                path = r['path'] if os.path.isabs(r['path']) else os.path.join(CD, r['path'])
                jobs.append(path)
                truth[path] = [(c, r['t0'] + u) for p, ti, c, dn, u in lets]
    print(len(jobs), 'receiver pieces', flush=True)
    with ThreadPoolExecutor(4) as ex:
        outs = list(ex.map(run, jobs))
    tot = collections.Counter()
    for path, out in zip(jobs, outs):
        lines = [l.rstrip('\n').split('\t') for l in open(out, encoding='utf-8') if l.strip() and not l.startswith('S')]
        sent = truth[path]
        st = np.array([t for c, t in sent])
        sc = [c for c, t in sent]
        for e in mix(lines):
            k = np.flatnonzero(np.abs(st - e['T']) <= TRUE_TOL)
            real_same = any(sc[i] == e['L'] for i in k)
            real_other = len(k) > 0 and not real_same
            what = 'real, right' if real_same else ('real, wrong letter' if real_other else 'noise')
            tot[(e['state'], what)] += 1
            if e['state'] == 'replaced':
                tot[('replaced', 'net right' if any(sc[i] == e.get('net') for i in k) else 'net wrong')] += 1
    for state in ('confirmed', 'replaced', 'grey', 'unchecked'):
        row = {w: tot[(state, w)] for w in ('real, right', 'real, wrong letter', 'noise')}
        print('%-10s %s' % (state, row))
    print('replaced: net right %d, net wrong %d' % (tot[('replaced', 'net right')], tot[('replaced', 'net wrong')]))


if __name__ == '__main__':
    main()
