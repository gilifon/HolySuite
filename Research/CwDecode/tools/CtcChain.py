"""
THE LOSS, THE ENCODING AND THE DECODING - checked with no network at all.

Hand-build the perfect answer for a known word: "nothing" at every step except one step per letter
where that letter is certain. The CTC loss of a perfect answer must be close to zero, and the greedy
decoder must read the word back. If either fails, the bug is here and no training could ever work.
Then a bidirectional network on the same tiny task, which separates "cannot learn at all" from
"cannot learn looking only backwards".
"""

import os
import sys

import numpy as np
import torch
import torch.nn as nn

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import BLANK, CLASSES, ALPHABET, encode, greedy  # noqa: E402
from CtcProbe import make  # noqa: E402


def perfect(word, steps):
    code = encode(word)
    logits = torch.full((steps, CLASSES), -20.0)
    logits[:, BLANK] = 20.0
    for k, c in enumerate(code):
        at = int((k + 1) * steps / (len(code) + 1))
        logits[at, :] = -20.0
        logits[at, c] = 20.0
    return logits, code


def main():
    print("1. perfect hand-built answers")
    ctc = nn.CTCLoss(blank=BLANK)
    for word in ["E", "TU", "CQ", "5NN", "R R"]:
        logits, code = perfect(word, 40)
        lp = logits.log_softmax(1).unsqueeze(1)
        loss = ctc(lp, torch.tensor(code), torch.tensor([40]), torch.tensor([len(code)]))
        print("   %-5s code %-18s loss %.4f   greedy reads \"%s\"" % (word, code, loss.item(), greedy(logits)))

    print("\n2. alphabet round trip:", "".join(ALPHABET[c - 1] for c in encode("CQ DE 4Z5SL/P 599 HW?")))

    print("\n3. bidirectional LSTM on the same tiny memorising task")
    rng = np.random.RandomState(5)
    items = [make(rng, 2, 3, 2) for _ in range(16)]
    longest = max(len(f) for _, f in items)
    x = np.zeros((16, longest, 1), dtype=np.float32)
    targets, tl, il = [], [], []
    for k, (w, f) in enumerate(items):
        x[k, :len(f), 0] = f
        c = encode(w)
        targets += c
        tl.append(len(c))
        il.append(len(f))

    class Bi(nn.Module):
        def __init__(self):
            super().__init__()
            self.rnn = nn.LSTM(1, 64, 1, batch_first=True, bidirectional=True)
            self.linear = nn.Linear(128, CLASSES)

        def forward(self, v):
            h, _ = self.rnn(v)
            return self.linear(h)

    torch.manual_seed(1)
    model = Bi()
    opt = torch.optim.Adam(model.parameters(), lr=3e-3)
    xt = torch.from_numpy(x)
    for s in range(1, 401):
        lp = model(xt).log_softmax(2).transpose(0, 1)
        loss = ctc(lp, torch.tensor(targets), torch.tensor(il), torch.tensor(tl))
        opt.zero_grad()
        loss.backward()
        opt.step()
        if s in (100, 200, 400):
            with torch.no_grad():
                right = sum(greedy(model(xt[k:k + 1])[0]) == items[k][0] for k in range(16))
            print("   step %d  loss %.3f  memorised %d of 16" % (s, loss.item(), right))


if __name__ == "__main__":
    main()
