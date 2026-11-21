"""
WHERE IN TIME DOES THE NETWORK SAY A LETTER? Compares each letter's CTC spike with where the practice
sound put that letter (letter end + 0.8 dit), on clean generated samples. Used to line Net's letters up
with Plain's (which are timed by the end of the letter's last mark) in the decode window.
"""
import os
import sys
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
os.environ.setdefault('CKPT', 'colab40k')
from common import *
import gen
import own

rng = np.random.default_rng(7)
offs = []
for i in range(60):
    a, text, events = gen.make_sample(rng, ease=14.0)
    ev = [(t, c) for t, c in events if c != ' ']
    if not ev:
        continue
    for ch, t in own.decode_window(a):
        if ch == ' ':
            continue
        best = min(ev, key=lambda e: abs(e[0] - t) if e[1] == ch else 9e9)
        if best[1] == ch and abs(best[0] - t) < 0.5:
            offs.append(t - best[0])
offs = np.array(offs)
print(len(offs), "letters; spike minus (letter end + 0.8 dit): median %.3f s, 10%% %.3f, 90%% %.3f"
      % (np.median(offs), np.percentile(offs, 10), np.percentile(offs, 90)))
