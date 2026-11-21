"""
HIS ON-AIR SESSIONS AS READY-CUT WINDOWS, for training away from this PC (Colab) - 2026-10-19.

finetune_real.py finds its windows by aligning every receiver's recording to FistKeyer's log, which needs the raw
recordings and minutes of work. This writes the windows it would use, already cut, the same way w1awdata.py cuts
W1AW: <out>.npy holds the audio (float16 [n, 15 s at 3200 Hz]) and <out>.json one row per window with its label,
the letter times (for the frame head), and its kind - train, hold (the last parts of each session, only ever
scored) or quiet (real empty band, labelled with nothing).

  export_real.py <out prefix> <session dir> [<session dir> ...]
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *
import finetune_real as fr


def main():
    out, dirs = sys.argv[1], sys.argv[2:]
    pool = fr.Pool(dirs)
    rows = []
    for kind in ('train', 'hold', 'quiet'):
        for iw in pool.windows[kind]:
            rows.append((kind, iw))
    audio = np.lib.format.open_memmap(out + '.npy', mode='w+', dtype=np.float16, shape=(len(rows), fr.N))
    meta = []
    for n, (kind, iw) in enumerate(rows):
        x, txt, ev = pool.sample(iw)
        audio[n] = x.astype(np.float16)
        path, s, t0, db = iw[1]
        meta.append({'kind': kind, 'label': txt, 'events': [[float(t), c] for t, c in ev], 'db': float(db),
                     'session': os.path.basename(os.path.normpath(dirs[iw[0]])), 'rx': os.path.basename(path), 'start': float(s)})
    audio.flush()
    json.dump(meta, open(out + '.json', 'w'))
    counts = {k: sum(1 for m in meta if m['kind'] == k) for k in ('train', 'hold', 'quiet')}
    print("wrote", out, counts, os.path.getsize(out + '.npy') // (1 << 20), "MB")


if __name__ == '__main__':
    main()
