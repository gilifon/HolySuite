"""
IS THE NETWORK IGNORING WHAT IT HEARS?

Every memorising trial stopped at exactly 2 of 16, whatever was changed - the signature of a
network that says the same thing for every input and happens to match two examples. This prints,
after training, what the network says for each example beside what was sent, and how different its
outputs are from one input to the next. Identical answers for different inputs means the input is
not getting through.
"""

import os
import sys

import numpy as np
import torch
import torch.nn as nn

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import BLANK, CLASSES, encode, greedy  # noqa: E402
from CtcProbe import make, Net  # noqa: E402


def main():
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

    print("input check: shape %s, nonzero share %.2f, per-example sums %s"
          % (x.shape, (x != 0).mean(), [int(x[k].sum()) for k in range(4)]))

    torch.manual_seed(1)
    model = Net("lstm", 64, 0.0)
    ctc = nn.CTCLoss(blank=BLANK)
    opt = torch.optim.Adam(model.parameters(), lr=1e-2)
    xt = torch.from_numpy(x)
    for s in range(600):
        lp = model(xt).log_softmax(2).transpose(0, 1)
        loss = ctc(lp, torch.tensor(targets), torch.tensor(il), torch.tensor(tl))
        opt.zero_grad()
        loss.backward()
        opt.step()

    with torch.no_grad():
        full = model(xt)
        alone = [model(xt[k:k + 1, :il[k]])[0] for k in range(16)]

    print("\nsent    batch-read   read alone (unpadded)")
    for k in range(16):
        print("  %-5s  %-11s  %s" % (items[k][0], greedy(full[k][:il[k]]), greedy(alone[k])))

    spread = (full[:, :20, :] - full[:1, :20, :]).abs().mean().item()
    print("\nhow much the first 20 outputs differ between inputs: %.4f" % spread)
    g = [p.grad for p in model.rnn.parameters()]
    print("gradient size on the recurrent layer after the last step: %s" % ["%.2e" % t.abs().mean().item() for t in g])


if __name__ == "__main__":
    main()
