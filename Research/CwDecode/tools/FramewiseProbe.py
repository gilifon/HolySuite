"""
TELL IT WHERE THE LETTERS ARE, INSTEAD OF MAKING IT HUNT.

CTC learning from nothing collapsed on this data - the network said "A" for every input and never
climbed out, because until a letter lands roughly in the right place it gets almost no clue which
letter. But the generator knows exactly when every letter was sent, which most CTC training does not.

So: label every moment. "Nothing" almost everywhere, and the LETTER itself during the gap straight
after that letter ends - the first moment a listener can know it was an S and not an I. This is
dense teaching, the kind that made the element network learn to 93.6%, but it names whole letters,
so there is nothing to count and nothing to miscount. And it only ever answers after the letter,
so it runs live.

Decoded the same way as CTC: likeliest answer each moment, repeats merged, "nothing" dropped.
Checked here on the same tiny task, both memorising and on examples it has never seen.
"""

import os
import sys

import numpy as np
import torch
import torch.nn as nn

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import BLANK, CLASSES, encode, greedy  # noqa: E402

MORSE = {'A': ".-", 'D': "-..", 'E': ".", 'I': "..", 'K': "-.-", 'M': "--", 'N': "-.",
         'R': ".-.", 'S': "...", 'T': "-", 'U': "..-", 'O': "---"}


def make(rng, unit_lo=2, unit_hi=3, letters_hi=2):
    word = "".join(rng.choice(list(MORSE)) for _ in range(rng.randint(1, letters_hi + 1)))
    unit = rng.randint(unit_lo, unit_hi + 1)
    frames, labels = [], []

    def hold(n, value, label=BLANK):
        frames.extend([value] * n)
        labels.extend([label] * n)

    hold(unit * 3, 0.0)
    for c, ch in enumerate(word):
        for e, el in enumerate(MORSE[ch]):
            if e:
                hold(unit, 0.0)
            hold(unit * (3 if el == '-' else 1), 1.0)
        # The gap after the letter: one unit of "nothing" (the letter could still continue), then
        # the letter named for one unit, then the rest of the gap.
        hold(unit, 0.0)
        hold(unit, 0.0, encode(ch)[0])
        hold(unit, 0.0)
    return word, np.array(frames, dtype=np.float32), np.array(labels, dtype=np.int64)


class Net(nn.Module):
    def __init__(self, hidden=64):
        super().__init__()
        self.lstm = nn.LSTM(1, hidden, 1, batch_first=True)
        self.linear = nn.Linear(hidden, CLASSES)

    def forward(self, x):
        h, _ = self.lstm(x)
        return self.linear(h)


def pack(items):
    longest = max(len(f) for _, f, _ in items)
    x = np.zeros((len(items), longest, 1), dtype=np.float32)
    y = np.full((len(items), longest), -100, dtype=np.int64)    # -100: ignored, the padding
    for k, (_, f, l) in enumerate(items):
        x[k, :len(f), 0] = f
        y[k, :len(l)] = l
    return torch.from_numpy(x), torch.from_numpy(y)


def score(model, items):
    with torch.no_grad():
        return sum(greedy(model(torch.from_numpy(f).view(1, -1, 1))[0]) == w for w, f, _ in items)


def main():
    rng = np.random.RandomState(5)
    train = [make(rng) for _ in range(16)]
    unseen = [make(np.random.RandomState(99 + i)) for i in range(100)]

    # "Nothing" is most of every sequence; weighted down so the letters are not drowned out.
    weights = torch.ones(CLASSES)
    weights[BLANK] = 0.1

    torch.manual_seed(1)
    model = Net()
    loss_of = nn.CrossEntropyLoss(weight=weights, ignore_index=-100)
    opt = torch.optim.Adam(model.parameters(), lr=3e-3)
    x, y = pack(train)

    print("MEMORISING 16")
    for s in range(1, 401):
        out = model(x)
        loss = loss_of(out.reshape(-1, CLASSES), y.reshape(-1))
        opt.zero_grad()
        loss.backward()
        opt.step()
        if s in (50, 100, 200, 400):
            print("  step %3d  loss %.3f  memorised %2d of 16" % (s, loss.item(), score(model, train)))

    print("\nLEARNING - fresh examples every step, judged on 100 it never sees")
    torch.manual_seed(2)
    model = Net()
    opt = torch.optim.Adam(model.parameters(), lr=3e-3)
    for s in range(1, 1501):
        xb, yb = pack([make(rng) for _ in range(32)])
        out = model(xb)
        loss = loss_of(out.reshape(-1, CLASSES), yb.reshape(-1))
        opt.zero_grad()
        loss.backward()
        opt.step()
        if s in (250, 500, 1000, 1500):
            print("  step %4d  loss %.3f  unseen read exactly %3d of 100" % (s, loss.item(), score(model, unseen)))
            sys.stdout.flush()


if __name__ == "__main__":
    main()
