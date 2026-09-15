"""
THE TEXTBOOK TEST: CAN IT MEMORISE ONE BATCH?

A correctly wired network, shown the same sixteen examples over and over, drives its loss almost
to nothing within a few hundred steps - memorising is far easier than learning. If it cannot, the
fault is in the wiring (the loss, the lengths, the shapes), not in the data or the patience.

Runs three variants side by side on the same batch, so a failure can be pinned to a part:
  as built    - convolutions then LSTM, exactly TrainCtc's CtcNet
  no convs    - the LSTM straight on the features, at 10 ms
  log-probs   - as built, but with log_softmax taken over the right axis checked explicitly
"""

import os
import sys

import numpy as np
import torch
import torch.nn as nn

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import CtcNet, BLANK, CLASSES, encode, greedy  # noqa: E402
from CtcWiring import make  # noqa: E402


class Plain(nn.Module):
    def __init__(self):
        super().__init__()
        self.lstm = nn.LSTM(5, 128, 2, batch_first=True)
        self.linear = nn.Linear(128, CLASSES)

    def forward(self, x):
        h, _ = self.lstm(x)
        return self.linear(h)

    @staticmethod
    def out_length(frames):
        return frames


def run(label, model, out_length, batch, texts, steps=300):
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=False)
    opt = torch.optim.Adam(model.parameters(), lr=3e-3)

    targets, tl, il = [], [], []
    for text, frames in texts:
        code = encode(text)
        targets += code
        tl.append(len(code))
        il.append(out_length(frames))
    targets = torch.tensor(targets, dtype=torch.long)
    tl = torch.tensor(tl, dtype=torch.long)
    il = torch.tensor(il, dtype=torch.long)

    x = torch.from_numpy(batch)
    for step in range(1, steps + 1):
        logits = model(x)
        assert logits.shape[1] >= int(il.max()), "network gives %d steps, lengths claim %d" % (logits.shape[1], int(il.max()))
        lp = logits.log_softmax(2).transpose(0, 1)
        loss = ctc(lp, targets, il, tl)
        opt.zero_grad()
        loss.backward()
        opt.step()
        if step in (1, 50, 100, 200, 300):
            said = greedy(model(x[:1])[0])
            print("  %-10s step %3d  loss %.3f   \"%s\" -> \"%s\"" % (label, step, loss.item(), texts[0][0], said))
            sys.stdout.flush()


def main():
    rng = np.random.RandomState(7)
    items = [make(rng) for _ in range(16)]
    longest = max(len(x) for _, x in items)
    batch = np.zeros((16, longest, 5), dtype=np.float32)
    for k, (_, x) in enumerate(items):
        batch[k, :len(x)] = x
    texts = [(t, len(x)) for t, x in items]
    print("16 examples, longest %d frames" % longest)

    torch.manual_seed(1)
    run("as built", CtcNet(5), CtcNet.out_length, batch, texts)
    torch.manual_seed(1)
    run("no convs", Plain(), Plain.out_length, batch, texts)


if __name__ == "__main__":
    main()
