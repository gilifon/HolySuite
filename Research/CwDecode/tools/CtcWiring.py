"""
IS THE CTC TRAINER PUT TOGETHER RIGHT?

When a CTC network sits at "nothing, everywhere" and never moves, the first question is not the data
- it is whether the model, the loss and the decoding are wired correctly. So this feeds the very same
CtcNet, loss and greedy decoder with readings that could not be easier: perfect square-wave CW in
the note's feature, silence at zero, nothing else. A correctly wired trainer learns that inside a
couple of minutes. One that does not has a bug in it, and no amount of better audio would help.
"""

import os
import sys
import time

import numpy as np
import torch
import torch.nn as nn

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from TrainCtc import CtcNet, BLANK, encode, greedy, edit_distance  # noqa: E402

MORSE = {'A': ".-", 'B': "-...", 'C': "-.-.", 'D': "-..", 'E': ".", 'F': "..-.", 'G': "--.",
         'H': "....", 'I': "..", 'K': "-.-", 'L': ".-..", 'M': "--", 'N': "-.", 'O': "---",
         'R': ".-.", 'S': "...", 'T': "-", 'U': "..-", '5': ".....", '7': "--...", '3': "...--"}
LETTERS = list(MORSE.keys())


def make(rng):
    words = ["".join(rng.choice(LETTERS) for _ in range(rng.randint(1, 4))) for _ in range(rng.randint(1, 3))]
    text = " ".join(words)
    unit = rng.randint(6, 12)                       # 60-120 ms a dit at 10 ms frames
    frames = [0.0] * (unit * 5)
    for w, word in enumerate(words):
        if w:
            frames += [0.0] * (unit * 7)
        for c, ch in enumerate(word):
            if c:
                frames += [0.0] * (unit * 3)
            for e, el in enumerate(MORSE[ch]):
                if e:
                    frames += [0.0] * unit
                frames += [2.5] * (unit * (3 if el == '-' else 1))
    frames += [0.0] * (unit * 5)
    x = np.zeros((len(frames), 5), dtype=np.float32)
    x[:, 2] = frames
    return text, x


def main(minutes=3.0):
    rng = np.random.RandomState(1)
    torch.manual_seed(1)
    model = CtcNet(5)
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=True)
    opt = torch.optim.Adam(model.parameters(), lr=3e-3)
    test = [make(rng) for _ in range(40)]

    started, step = time.time(), 0
    while time.time() - started < minutes * 60:
        items = [make(rng) for _ in range(16)]
        longest = max(len(x) for _, x in items)
        batch = np.zeros((len(items), longest, 5), dtype=np.float32)
        targets, tl, il = [], [], []
        for k, (text, x) in enumerate(items):
            batch[k, :len(x)] = x
            code = encode(text)
            targets += code
            tl.append(len(code))
            il.append(CtcNet.out_length(len(x)))
        lp = model(torch.from_numpy(batch)).log_softmax(-1).transpose(0, 1)
        loss = ctc(lp, torch.tensor(targets), torch.tensor(il), torch.tensor(tl))
        opt.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 5.0)
        opt.step()
        step += 1

        if step % 50 == 0:
            model.eval()
            err = chars = 0
            with torch.no_grad():
                for text, x in test:
                    said = greedy(model(torch.from_numpy(x).unsqueeze(0))[0])
                    err += edit_distance(said, text)
                    chars += len(text)
            model.train()
            print("step %4d  %3.0fs  loss %.3f  char error %.1f%%   e.g. \"%s\" -> \"%s\""
                  % (step, time.time() - started, loss.item(), 100.0 * err / chars, test[0][0],
                     greedy(model(torch.from_numpy(test[0][1]).unsqueeze(0))[0])))
            sys.stdout.flush()


if __name__ == "__main__":
    main(float(sys.argv[1]) if len(sys.argv) > 1 else 3.0)
