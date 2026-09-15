"""
WHY CAN IT NOT MEMORISE? - one variable at a time, on a batch small enough to be quick.

The overfit test showed neither the convolution version nor a plain LSTM can memorise sixteen clean
examples, so the fault is in what they share. Each line below changes exactly one thing from the
baseline and reports the loss after the same number of steps on the same batch. The one that drops
to near zero names the culprit.
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


def make(rng, unit_lo, unit_hi, letters_hi):
    word = "".join(rng.choice(list(MORSE)) for _ in range(rng.randint(1, letters_hi + 1)))
    unit = rng.randint(unit_lo, unit_hi + 1)
    frames = [0.0] * (unit * 3)
    for c, ch in enumerate(word):
        if c:
            frames += [0.0] * (unit * 3)
        for e, el in enumerate(MORSE[ch]):
            if e:
                frames += [0.0] * unit
            frames += [1.0] * (unit * (3 if el == '-' else 1))
    frames += [0.0] * (unit * 3)
    return word, np.array(frames, dtype=np.float32)


class Net(nn.Module):
    def __init__(self, cell, hidden, forget_bias):
        super().__init__()
        self.rnn = (nn.GRU if cell == "gru" else nn.LSTM)(1, hidden, 1, batch_first=True)
        if cell == "lstm" and forget_bias:
            for name, p in self.rnn.named_parameters():
                if "bias" in name:
                    n = p.shape[0] // 4
                    p.data[n:2 * n].fill_(forget_bias / 2.0)   # both biases add up
        self.linear = nn.Linear(hidden, CLASSES)

    def forward(self, x):
        h, _ = self.rnn(x)
        return self.linear(h)


def trial(label, rng_seed=5, unit=(2, 3), letters=2, cell="lstm", hidden=64, lr=3e-3,
          forget_bias=0.0, clip=None, steps=250, reduction="mean", scale=1.0):
    rng = np.random.RandomState(rng_seed)
    items = [make(rng, unit[0], unit[1], letters) for _ in range(16)]
    longest = max(len(f) for _, f in items)
    x = np.zeros((16, longest, 1), dtype=np.float32)
    targets, tl, il = [], [], []
    for k, (w, f) in enumerate(items):
        x[k, :len(f), 0] = f * scale
        code = encode(w)
        targets += code
        tl.append(len(code))
        il.append(len(f))

    torch.manual_seed(1)
    model = Net(cell, hidden, forget_bias)
    ctc = nn.CTCLoss(blank=BLANK, reduction=reduction)
    opt = torch.optim.Adam(model.parameters(), lr=lr)
    xt = torch.from_numpy(x)
    T, TL, IL = torch.tensor(targets), torch.tensor(tl), torch.tensor(il)

    for s in range(steps):
        lp = model(xt).log_softmax(2).transpose(0, 1)
        loss = ctc(lp, T, IL, TL)
        opt.zero_grad()
        loss.backward()
        if clip:
            torch.nn.utils.clip_grad_norm_(model.parameters(), clip)
        opt.step()

    with torch.no_grad():
        right = sum(greedy(model(xt[k:k + 1])[0]) == items[k][0] for k in range(16))
    print("  %-34s longest %4d frames  loss %6.3f   memorised %2d of 16" % (label, longest, loss.item(), right))
    sys.stdout.flush()


def main():
    trial("baseline, 250 steps")
    trial("lr 1e-2", lr=1e-2)
    trial("lr 3e-2, clip 1", lr=3e-2, clip=1.0)
    trial("input x5", scale=5.0)
    trial("input x5, lr 1e-2", scale=5.0, lr=1e-2)
    trial("baseline, 2000 steps", steps=2000)
    trial("input x5, lr 1e-2, 2000 steps", scale=5.0, lr=1e-2, steps=2000)


if __name__ == "__main__":
    main()
