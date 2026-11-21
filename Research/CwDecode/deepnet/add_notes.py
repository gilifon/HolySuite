"""
THE NOTE OF THE STATION IN EACH READY-CUT WINDOW (2026-10-19), for the centred network (common.spectrogram_centred).

  add_notes.py own <prefix> <session dir> [...]   his sessions: the note the alignment found his signal on, per receiver piece
  add_notes.py w1aw <prefix>                      W1AW: the steadiest strong note in the window (W1AW is the station there)

Writes 'note' (Hz) into <prefix>.json.
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *


def strongest(x):
    pad = FFT // 2
    a = np.pad(np.asarray(x, dtype=np.float32), (pad, pad), mode='reflect')
    frames = 1 + (len(a) - FFT) // HOP
    idx = np.arange(FFT)[None, :] + HOP * np.arange(frames)[:, None]
    s = 20 * np.log10(np.abs(np.fft.rfft(a[idx] * WINDOW, n=FFT, axis=1))[:, BIN0:BIN1] + 1e-9)
    lift = np.percentile(s, 95, axis=0) - np.percentile(s, 30, axis=0)
    return (BIN0 + int(np.argmax(lift))) * HZ_PER_BIN


def main():
    kind, prefix = sys.argv[1], sys.argv[2]
    meta = json.load(open(prefix + '.json'))
    if kind == 'own':
        hz = {}
        for d in sys.argv[3:]:
            for r in json.load(open(os.path.join(d, 'align.json'))):
                hz[(os.path.basename(os.path.normpath(d)), os.path.basename(r['path']))] = float(r['hz'])
        miss = 0
        for m in meta:
            m['note'] = hz.get((m['session'], m['rx']))
            if m['note'] is None:
                miss += 1
                m['note'] = 700.0
        print("notes from the alignment; %d windows had none" % miss)
    else:
        audio = np.load(prefix + '.npy', mmap_mode='r')
        for i, m in enumerate(meta):
            m['note'] = strongest(audio[i])
        print("W1AW notes: median %.0f Hz" % np.median([m['note'] for m in meta]))
    json.dump(meta, open(prefix + '.json', 'w'))


if __name__ == '__main__':
    main()
