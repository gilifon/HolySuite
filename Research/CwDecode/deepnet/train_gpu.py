"""
TRAINING THE NETWORK ON A GPU (Google Colab's free one, or any CUDA card) - and on a CPU for a smoke test.

  train_gpu.py --out DIR [--size small|base|large] [--batch 64] [--steps 100000] [--workers 2] [--init CKPT] [--lr 1e-3]

--init starts from a network already trained (its weights only) instead of from zero, with no easy first steps:
the second run (2026-10-17) goes on from the first one's best.

The practice sound is made by gen.py in worker processes (CPU); the spectrogram and the network run on the
GPU in half precision. RESUMABLE: it writes DIR/ckpt_last.pt every 500 steps and picks up from it when started
again - Colab ends a free session after some hours, so start it again and it goes on.

Files written in DIR: ckpt_last.pt, ckpt_best.pt (the lowest validation error), train.log
"""
import argparse
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
SIZES = {
    'small': dict(d=96, heads=4, ff=3, blocks=3, ch=(8, 16, 16)),
    'base': dict(d=192, heads=4, ff=4, blocks=6, ch=(16, 32, 32)),        # about the size of DeepCW's published model (3.6 M numbers)
    'large': dict(d=256, heads=4, ff=4, blocks=8, ch=(16, 32, 32)),
}
TOK = ((((1 + int(SEC * RATE) // HOP) + 1) // 2) + 1) // 2


class Stream(torch.utils.data.IterableDataset):
    def __init__(self, ease):
        self.ease = ease

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        wid = info.id if info else 0
        rng = np.random.default_rng([987, wid, int(time.time() * 1000) % 1000000])
        torch.set_num_threads(1)
        while True:
            a, txt, ev = gen.make_sample(rng, SEC, ease=self.ease.value)
            yield a, gen.encode(txt), frame_targets(ev)


def frame_targets(events):
    t = np.full(TOK, BLANK, dtype=np.int64)
    for sec, c in events:
        k = int(round(sec * RATE / (4 * HOP)))
        if 0 <= k < TOK and c in C2I:
            t[k] = C2I[c]
    return t


def collate(batch):
    x = torch.from_numpy(np.stack([b[0] for b in batch]))
    ys = [torch.from_numpy(b[1]) for b in batch]
    lens = torch.tensor([len(y) for y in ys], dtype=torch.long)
    y = torch.cat(ys) if ys else torch.zeros(0, dtype=torch.long)
    ft = torch.from_numpy(np.stack([b[2] for b in batch]))
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
    out, prev = [], None
    for k in lp.argmax(-1).tolist():
        if k != BLANK and k != prev:
            out.append(CHARS[k])
        prev = k
    return ''.join(out)


def validation_set(n=160):
    rng = np.random.default_rng(777)
    xs, ts = [], []
    for _ in range(n):
        a, t, _ = gen.make_sample(rng, SEC)
        xs.append(a); ts.append(t)
    return np.stack(xs), ts


def evaluate(net, vx, vt, dev, show=3):
    net.eval()
    errs = chars = 0
    shown = []
    with torch.no_grad():
        for i in range(0, len(vx), 32):
            x = spectrogram_torch(torch.from_numpy(vx[i:i + 32]).to(dev))
            with torch.autocast(dev.type, dtype=torch.float16, enabled=dev.type == 'cuda'):
                lp = net(x)
            lp = lp.float().cpu()
            for j in range(lp.shape[0]):
                hyp = greedy(lp[j]); ref = vt[i + j]
                errs += edit(ref, hyp); chars += max(1, len(ref))
                if len(shown) < show and i == 0 and j % 50 == 0:
                    shown.append((ref, hyp))
    net.train()
    return errs / chars, shown


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--size', default='base', choices=list(SIZES))
    ap.add_argument('--batch', type=int, default=64)
    ap.add_argument('--steps', type=int, default=100000)
    ap.add_argument('--workers', type=int, default=max(1, (os.cpu_count() or 2)))
    ap.add_argument('--lr', type=float, default=1e-3)
    ap.add_argument('--init', default=None)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    dev = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
    gen.load()
    net = Net(**SIZES[a.size]).to(dev)
    opt = torch.optim.AdamW(net.parameters(), lr=a.lr, weight_decay=0.01)
    scaler = torch.amp.GradScaler(enabled=dev.type == 'cuda')
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=True, reduction='none')
    wts = torch.ones(NCLASS)
    wts[:BLANK] = 6.0
    ce = nn.CrossEntropyLoss(weight=wts.to(dev))
    step = 0
    ck = os.path.join(a.out, 'ckpt_last.pt')
    best = 9.0
    if os.path.exists(ck):
        z = torch.load(ck, map_location=dev, weights_only=False)
        net.load_state_dict(z['net'])
        opt.load_state_dict(z['opt'])
        step = z['step']
        best = z.get('best', 9.0)
        if 'scaler' in z and dev.type == 'cuda':
            scaler.load_state_dict(z['scaler'])
    elif a.init:
        z = torch.load(a.init, map_location=dev, weights_only=False)
        net.load_state_dict(z['net'])
    log = open(os.path.join(a.out, 'train.log'), 'a')

    def say(s):
        print(s, flush=True)
        log.write(s + '\n')
        log.flush()

    say("device %s, size %s, params %d, batch %d, workers %d, from step %d of %d" % (dev, a.size, n_params(net), a.batch, a.workers, step, a.steps))
    vx, vt = validation_set()
    if a.init and step == 0:
        cer, _ = evaluate(net, vx, vt, dev)
        say("  VALIDATION before any step (the network it starts from): character error rate %.3f" % cer)
    ease = mp.Value('d', 0.0 if a.init else 14.0)
    loader = torch.utils.data.DataLoader(Stream(ease), batch_size=a.batch, num_workers=a.workers, collate_fn=collate,
                                         prefetch_factor=4, persistent_workers=True)
    t0 = time.time()
    step0 = step
    for audio, y, lens, ft in loader:
        if step >= a.steps:
            break
        warm = min(1.0, (step + 1) / (300 if a.init else 1000))
        lr = a.lr * warm * (0.05 + 0.95 * 0.5 * (1 + np.cos(np.pi * min(1.0, step / a.steps))))
        for g in opt.param_groups:
            g['lr'] = lr
        ease.value = 0.0 if a.init else (14.0 if step < 600 else max(0.0, 14.0 * (2400 - step) / 1800))
        with torch.no_grad():
            x = spectrogram_torch(audio.to(dev, non_blocking=True))
        with torch.autocast(dev.type, dtype=torch.float16, enabled=dev.type == 'cuda'):
            lp, fl = net(x)
        lp = lp.float()
        fl = fl.float()
        inl = torch.full((lp.shape[0],), lp.shape[1], dtype=torch.long)
        # per sample, divided by its length but never by less than 8: a sample with (almost) no text divided by
        # 1 gave spikes of 300+ that threw the first steps about
        l_ctc = (ctc(lp.transpose(0, 1).cpu(), y, inl, lens) / lens.clamp(min=8).float()).mean().to(dev)
        l_frame = ce(fl.reshape(-1, NCLASS), ft[:, :fl.shape[1]].to(dev).reshape(-1))
        loss = l_ctc + l_frame
        opt.zero_grad(set_to_none=True)
        scaler.scale(loss).backward()
        scaler.unscale_(opt)
        nn.utils.clip_grad_norm_(net.parameters(), 1.0)
        scaler.step(opt)
        scaler.update()
        step += 1
        if step % 50 == 0:
            el = time.time() - t0
            say("step %6d  ctc %.2f frame %.3f  lr %.2e ease %4.1f  %.2f s/step  %.0f samples/s" % (
                step, float(l_ctc), float(l_frame), lr, ease.value, el / (step - step0), (step - step0) * a.batch / el))
        if step % 500 == 0 or step == a.steps:
            cer, shown = evaluate(net, vx, vt, dev)
            say("  VALIDATION step %d: character error rate %.3f" % (step, cer))
            for ref, hyp in shown:
                say("     ref: " + ref[:90])
                say("     got: " + hyp[:90])
            if cer < best:
                best = cer
                torch.save({'net': net.state_dict(), 'step': step, 'cer': cer, 'size': a.size}, os.path.join(a.out, 'ckpt_best.pt'))
            torch.save({'net': net.state_dict(), 'opt': opt.state_dict(), 'scaler': scaler.state_dict(), 'step': step,
                        'best': best, 'size': a.size}, ck + '.tmp')
            os.replace(ck + '.tmp', ck)


if __name__ == '__main__':
    mp.freeze_support()
    main()
