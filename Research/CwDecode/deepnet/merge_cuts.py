"""
JOIN READY-CUT WINDOW SETS (2026-11-20): merge_cuts.py <out prefix> <prefix> [<prefix> ...]
-> one <out>.npy + <out>.json, as w1awdata.py cut makes, so finetune_gpu.py --w1aw takes several batches of nights.
"""
import json
import sys

import numpy as np

out, parts = sys.argv[1], sys.argv[2:]
metas = [json.load(open(p + '.json')) for p in parts]
arrs = [np.load(p + '.npy', mmap_mode='r') for p in parts]
for p, m, a in zip(parts, metas, arrs):
    assert len(m) == a.shape[0], p
n = sum(a.shape[0] for a in arrs)
mm = np.lib.format.open_memmap(out + '.npy', mode='w+', dtype=arrs[0].dtype, shape=(n, arrs[0].shape[1]))
i = 0
for a in arrs:
    mm[i:i + a.shape[0]] = a
    i += a.shape[0]
mm.flush()
json.dump([x for m in metas for x in m], open(out + '.json', 'w'), default=float)
print("%d windows from %s -> %s" % (n, ', '.join('%s %d' % (p, len(m)) for p, m in zip(parts, metas)), out))
