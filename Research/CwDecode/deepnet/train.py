"""
TRAINING THE SMALL NETWORK on the generated practice sound (gen.py), on this PC's CPU.

  train.py [steps] [batch]        resumes from deepnet/ckpt_last.pt when it exists

Curriculum: the first steps are easy (stations strong, little interference) because CTC sits at "print nothing"
for a long while unless it is first shown something it can read; the ease fades to zero by step 1800.
Every 250 steps: greedy CTC read of 120 fixed validation samples (character error rate), a few printed, a
checkpoint. Log: deepnet/train.log
"""
import os
import sys
import time
import numpy as np
import torch
import torch.nn as nn
import torch.multiprocessing as mp

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *
import gen
from model import Net, n_params

SEC = 15.0


class Stream(torch.utils.data.IterableDataset):
    def __init__(self, ease):
        self.ease = ease

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        wid = info.id if info else 0
        rng = np.random.default_rng([12345, wid, int(time.time()) % 100000])
        torch.set_num_threads(1)
        while True:
            a, txt, ev = gen.make_sample(rng, SEC, ease=self.ease.value)
            yield spectrogram(a), gen.encode(txt), frame_targets(ev)


TOK = ((((1 + 15 * RATE // HOP) + 1) // 2) + 1) // 2          # output steps of the network for a 15 s window


def frame_targets(events):
    """The letter said at each output step (BLANK = none): each letter sits one step, in the gap after it."""
    t = np.full(TOK, BLANK, dtype=np.int64)
    for sec, c in events:
        k = int(round(sec * RATE / (4 * HOP)))
        if 0 <= k < TOK and c in C2I:
            t[k] = C2I[c]
    return t


def collate(batch):
    x = torch.from_numpy(np.stack([b[0] for b in batch]))
    ft = torch.from_numpy(np.stack([b[2] for b in batch]))
    ys = [torch.from_numpy(b[1]) for b in batch]
    lens = torch.tensor([len(y) for y in ys], dtype=torch.long)
    y = torch.cat(ys) if len(ys) else torch.zeros(0, dtype=torch.long)
    return x, y, lens, ft


def edit(a, b):
    d = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        prev, d[0] = d[0], i
        for j in range(1, len(b) + 1):
            cur = d[j]
            d[j] = min(d[j] + 1, d[j - 1] + 1, prev + (a[i - 1] != b[j - 1]))
            prev = cur
    return d[len(b)]


def greedy(lp):
    best = lp.argmax(-1).tolist()
    out, prev = [], None
    for k in best:
        if k != BLANK and k != prev:
            out.append(CHARS[k])
        prev = k
    return ''.join(out)


def validation_set():
    path = os.path.join(HERE, 'val.npz')
    if os.path.exists(path):
        z = np.load(path, allow_pickle=True)
        return z['x'], list(z['t'])
    rng = np.random.default_rng(777)
    xs, ts = [], []
    for _ in range(120):
        a, t, _ = gen.make_sample(rng, SEC)
        xs.append(spectrogram(a)); ts.append(t)
    np.savez(path, x=np.stack(xs), t=np.array(ts, dtype=object))
    return np.stack(xs), ts


def frame_read(fl):
    """The frame head's text: the best class at each step, none dropped."""
    best = fl.argmax(-1).tolist()
    return ''.join(CHARS[k] for k in best if k != BLANK)


def evaluate(net, vx, vt, show=3):
    net.eval()
    errs = errs_f = chars = 0
    shown = []
    net.want_frames = True
    with torch.no_grad():
        for i in range(0, len(vx), 24):
            lp, fl = net(torch.from_numpy(vx[i:i + 24]))
            for j in range(lp.shape[0]):
                hyp = greedy(lp[j]); ref = vt[i + j]; hyp_f = frame_read(fl[j])
                errs += edit(ref, hyp); errs_f += edit(ref, hyp_f); chars += max(1, len(ref))
                if len(shown) < show and i == 0 and j % 40 == 0:
                    shown.append((ref, hyp, hyp_f))
    net.want_frames = False
    net.train()
    return (errs / chars, errs_f / chars), shown


def main():
    steps = int(sys.argv[1]) if len(sys.argv) > 1 else 4000
    batch = int(sys.argv[2]) if len(sys.argv) > 2 else 16
    torch.set_num_threads(4)
    gen.load()
    net = Net()
    opt = torch.optim.AdamW(net.parameters(), lr=1.5e-3, weight_decay=0.01)
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=True)
    step0 = 0
    ck = os.path.join(HERE, 'ckpt_last.pt')
    if os.path.exists(ck):
        z = torch.load(ck, weights_only=False)
        net.load_state_dict(z['net']); opt.load_state_dict(z['opt']); step0 = z['step']
    log = open(os.path.join(HERE, 'train.log'), 'a')

    def say(s):
        print(s, flush=True); log.write(s + '\n'); log.flush()

    say("params %d, batch %d, from step %d of %d" % (n_params(net), batch, step0, steps))
    vx, vt = validation_set()
    ease = mp.Value('d', 14.0)
    loader = torch.utils.data.DataLoader(Stream(ease), batch_size=batch, num_workers=3, collate_fn=collate, prefetch_factor=4)
    t0 = time.time(); run = 0.0; best = 9.0
    step = step0
    wts = torch.ones(NCLASS); wts[:BLANK] = 6.0
    ce = nn.CrossEntropyLoss(weight=wts)
    for x, y, lens, ft in loader:
        if step >= steps:
            break
        lr = 1.5e-3 * min(1.0, (step + 1) / 400) * (0.05 + 0.95 * 0.5 * (1 + np.cos(np.pi * min(1.0, step / steps))))
        for g in opt.param_groups:
            g['lr'] = lr
        ease.value = 14.0 if step < 500 else max(0.0, 14.0 * (1800 - step) / 1300)
        lp, fl = net(x)                                       # [B, T', C] both
        inl = torch.full((lp.shape[0],), lp.shape[1], dtype=torch.long)
        l_ctc = ctc(lp.transpose(0, 1), y, inl, lens)
        l_frame = ce(fl.reshape(-1, NCLASS), ft[:, :fl.shape[1]].reshape(-1))
        loss = l_ctc + l_frame
        opt.zero_grad(); loss.backward()
        nn.utils.clip_grad_norm_(net.parameters(), 1.0)
        opt.step()
        run = 0.98 * run + 0.02 * float(loss.detach()) if step > step0 else float(loss.detach())
        step += 1
        if step % 25 == 0:
            say("step %5d  loss %.3f (ctc %.2f frame %.3f)  lr %.2e  ease %4.1f  %.1f s/step" % (step, run, float(l_ctc), float(l_frame), lr, ease.value, (time.time() - t0) / (step - step0)))
        if step % 250 == 0 or step == steps:
            (cer, cer_f), shown = evaluate(net, vx, vt)
            say("  VALIDATION step %d: character error rate  CTC %.3f   frame head %.3f" % (step, cer, cer_f))
            for ref, hyp, hyp_f in shown:
                say("     ref:   " + ref[:90]); say("     ctc:   " + hyp[:90]); say("     frame: " + hyp_f[:90])
            cer = min(cer, cer_f)
            torch.save({'net': net.state_dict(), 'opt': opt.state_dict(), 'step': step}, ck)
            if cer < best:
                best = cer; torch.save({'net': net.state_dict(), 'step': step, 'cer': cer}, os.path.join(HERE, 'ckpt_best.pt'))


if __name__ == '__main__':
    mp.freeze_support()
    main()
