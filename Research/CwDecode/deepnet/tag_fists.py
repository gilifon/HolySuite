"""
WHICH HAND STYLE EACH REAL WINDOW WAS SENT IN (2026-11-20) - for training a version on the even styles only.

A window's place in the keyer's own time is its start in the recording minus the piece's t0 (where FistKeyer's
clock 0 sits in that recording, from realdata.align: align.json, or the export log's "t0" lines). keyer.log gives
each part's start time and fist. A window may cover the end of one part and the start of the next: it gets all
the fists it covers. Adds 'fists' (list) to every non-quiet row; also drops rows covering a keying stall.

  tag_fists.py <in prefix> <out prefix> <session>=<dir>[@<align source>] ...
    align source: an align.json, or an export log with "NAME.wav  t0 X" lines (default <dir>/align.json)
  env STALL="session:from_s-to_s,..."   keyer-time spans to drop (a keying stall)
"""
import json, os, re, sys
import numpy as np


def parts(keyer_log):
    out = []
    for l in open(keyer_log, encoding='utf-8', errors='replace'):
        m = re.match(r'\s*([\d.]+)s\s+part (\d+) \((\w+),', l)
        if m: out.append((float(m[1]), int(m[2]), m[3]))
    return out


def t0s(src):
    if src.endswith('.json'):
        return {os.path.basename(r['path'].replace(chr(92), '/')): r['t0'] for r in json.load(open(src))}
    d = {}
    for l in open(src, encoding='utf-8', errors='replace'):
        m = re.match(r'(\S+\.wav)\s+t0\s+(-?[\d.]+)', l)
        if m: d[m[1]] = float(m[2])
    return d


def main():
    inp, out, specs = sys.argv[1], sys.argv[2], sys.argv[3:]
    stall = {}
    for s in filter(None, os.environ.get('STALL', '').split(',')):
        n, span = s.split(':'); a, b = span.split('-'); stall.setdefault(n, []).append((float(a), float(b)))
    sess = {}
    for s in specs:
        name, rest = s.split('=', 1)
        d, src = (rest.split('@', 1) + [None])[:2]
        if src is None: src = os.path.join(d, 'align.json')
        sess[name] = (parts(os.path.join(d, 'keyer.log')), t0s(src))
    meta = json.load(open(inp + '.json'))
    keep, miss = [], 0
    for i, r in enumerate(meta):
        if r['kind'] != 'quiet':
            P, T = sess[r['session']]
            if r['rx'] not in T:
                miss += 1; r['fists'] = None
            else:
                a = r['start'] - T[r['rx']]; b = a + 15.0
                st = P
                fs = set()
                for j, (t, n, f) in enumerate(st):
                    end = st[j + 1][0] if j + 1 < len(st) else 1e9
                    if t < b and end > a: fs.add(f)
                r['fists'] = sorted(fs)
                if any(x < b and y > a for x, y in stall.get(r['session'], [])):
                    continue
        keep.append(i)
    arr = np.load(inp + '.npy', mmap_mode='r')
    o = np.lib.format.open_memmap(out + '.npy', mode='w+', dtype=arr.dtype, shape=(len(keep),) + arr.shape[1:])
    for k, i in enumerate(keep): o[k] = arr[i]
    o.flush()
    json.dump([meta[i] for i in keep], open(out + '.json', 'w'))
    print('kept', len(keep), 'of', len(meta), '; rows without a t0:', miss)


if __name__ == '__main__':
    main()
