"""
THE EXAM: a letter network against the plain decoder on 4Z5SL's own transmissions.

Three sessions of his IC-7610 heard in Europe, every letter sent known (SENT.txt), and none of it
ever used to train a network - the texts differ from everything trained on, so a network cannot pass
by having learned the words. Scored exactly as ScoreText.py scores the plain decoder.

  Exam.py weights.net [weights2.net ...]

Needs build\\exam_4z5sl*.bin (DumpFeatures --wav on each session) and build\\plain_4z5sl*.txt
(BulletinText.exe on each session) - both made beside it.
"""

import os
import sys

import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainLetters import LetterNet, read, load_weights  # noqa: E402
from TrainCtc import greedy  # noqa: E402
from ScoreText import words, compare  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SESSIONS = ["4z5sl", "4z5sl2", "4z5sl3"]


def score_lines(reference, lines):
    ref = words(open(reference, encoding="utf-8").read())
    total = [0, 0, 0]
    for said in lines:
        r, s, i = compare(ref, said)
        total[0] += r; total[1] += s; total[2] += i
    return total


def plain():
    total = [0, 0, 0]
    for s in SESSIONS:
        lines = [l.split("\t", 1)[1] for l in open(os.path.join(ROOT, "build", "plain_%s.txt" % s), encoding="utf-8-sig")
                 if "\t" in l]
        t = score_lines(os.path.join(ROOT, "recordings", s, "SENT.txt"), lines)
        total = [a + b for a, b in zip(total, t)]
    return total


def gates(session):
    """name -> per-frame level of the plain decoder's belief (GateMask.ps1: 0 none, 1 station, 2 Morse),
    two 5 ms readings to a 10 ms frame, the higher of the two."""
    out = {}
    path = os.path.join(ROOT, "build", "gate_%s.txt" % session)
    if not os.path.exists(path):
        return out
    for line in open(path, encoding="utf-8"):
        name, mask = line.rstrip("\n").split("\t", 1)
        levels = [int(c) for c in mask]
        out[name] = [max(levels[i:i + 2]) for i in range(0, len(levels), 2)]
    return out


def network(weights, gate=0, widen=0):
    """gate 0: the network alone. gate 1 or 2: it is heard only in frames where the plain decoder's
    belief is at least that high, widened by `widen` frames either side."""
    from TrainCtc import BLANK
    total = [0, 0, 0]
    for s in SESSIONS:
        examples, bins = read(os.path.join(ROOT, "build", "exam_%s.bin" % s))
        belief = gates(s)
        model = LetterNet(bins)
        load_weights(model, weights)
        model.eval()
        lines = []
        with torch.no_grad():
            for name, _, d, _ in examples:
                logits = model(torch.from_numpy(d).unsqueeze(0))[0]
                if gate and name in belief:
                    levels = belief[name]
                    open_ = [False] * len(logits)
                    for j in range(len(logits)):
                        if j < len(levels) and levels[j] >= gate:
                            for k in range(max(0, j - widen), min(len(logits), j + widen + 1)):
                                open_[k] = True
                    shut = torch.tensor([not o for o in open_])
                    logits[shut] = -1e9
                    logits[shut, BLANK] = 0
                lines.append(greedy(logits))
        t = score_lines(os.path.join(ROOT, "recordings", s, "SENT.txt"), lines)
        total = [a + b for a, b in zip(total, t)]
    return total


if __name__ == "__main__":
    r, s, i = plain()
    print("%-28s read %4d of %4d   invented %4d" % ("plain decoder", r, s, i))
    for w in [a for a in sys.argv[1:] if not a.startswith("--")]:
        r, s, i = network(w)
        print("%-28s read %4d of %4d   invented %4d" % (os.path.basename(w), r, s, i))
        if "--gated" in sys.argv:
            for gate, widen in ((1, 0), (1, 30), (2, 0), (2, 30), (2, 100)):
                r, s, i = network(w, gate, widen)
                print("   %s, widened %.1f s  read %4d of %4d   invented %4d"
                      % ("only where a station stands out" if gate == 1 else "only where proved Morse  ", widen / 100.0, r, s, i))
