"""
TEACHING ON REAL ON-AIR SOUND, ON A GPU (Colab) - 2026-10-19. The same teaching as finetune_real.py, but from
ready-cut windows (export_real.py for his sessions, w1awdata.py cut for W1AW), so it needs no raw recordings,
and on the GPU in half precision: about 7 hours on his PC become well under an hour on Colab's T4.

Every batch: generated practice (gen.py, 4% silent as in run 1), his on-air windows (8% of them real empty
band; half with more real band noise laid on), and W1AW windows (30% with noise laid on) - in the shares given.
Held out and only scored: his sessions' last parts, and W1AW's held-out bulletins on the non-French receivers
(the French copy's labels came from the network's own reading). The best network is the lowest MEAN of the two.

RESUMABLE: DIR/ckpt_last.pt every 250 steps; run again with the same --out and it goes on.

--centred (2026-10-19): the network is told which station to read - every frame is centred on the followed note
(common.spectrogram_centred). Generated practice is then a crowded band with only the followed station in the label
(gen.make_sample with_track); his windows and W1AW's are centred on their station's note ('note', add_notes.py),
a few Hz off as Plain's would be; empty-band windows on a note anywhere.

  finetune_gpu.py --own own_cut --w1aw w1aw_cut --init init.pt --out DIR [--steps 2000] [--batch 32] [--lr 1e-4]
                  [--own-share 0.25] [--w1aw-share 0.25] [--w1aw-hold ARLP042,ARLD041,ARLD038,ARLD039]

--scratch SIZE instead of --init (2026-11-19, never tried before): a NEW network from zero, with the real windows in
from the start - every earlier real-sound run only adjusted a network first taught on practice sound alone, and by
2026-10-28 that adjusting had stopped gaining. As train_gpu.py did from zero: lr 1e-3 with 1000 warm-up steps and
easy practice sound at first (ease 14 for 600 steps, down to 0 by step 2400); the real shares grow from 0 to the
given ones over the first 3000 steps, as a network that cannot read yet learns nothing from hard real sound.
"""
import argparse
import json
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
from train_gpu import SIZES, SEC, frame_targets, collate, edit, greedy

N = int(SEC * RATE)


FRAMES = 1 + N // HOP


def centres_for(note, rng):
    """Centre bins for a whole window on one station's note, a few Hz off as Plain's measurement is."""
    tr = np.full(FRAMES, float(note) + rng.uniform(-8, 8)) + rng.normal(0, 2.0, FRAMES)
    return centre_bins(tr, FRAMES)


def collate_c(batch):
    x, y, lens, ft = collate([b[:3] for b in batch])
    return x, y, lens, ft, torch.from_numpy(np.stack([b[3] for b in batch]))


class Cut:
    """Ready-cut windows: <prefix>.npy audio, <prefix>.json labels."""
    def __init__(self, prefix, train, hold):
        self.prefix = prefix
        self.meta = json.load(open(prefix + '.json'))
        self.audio = None
        self.windows = {'train': [i for i, m in enumerate(self.meta) if train(m)],
                        'hold': [i for i, m in enumerate(self.meta) if hold(m)]}

    def sample(self, i):
        if self.audio is None:
            self.audio = np.load(self.prefix + '.npy', mmap_mode='r')
        m = self.meta[i]
        return np.asarray(self.audio[i], dtype=np.float32), m['label'], [(t, c) for t, c in m['events']]


class Mixed(torch.utils.data.IterableDataset):
    def __init__(self, own, quiet, w1aw, own_share, w1aw_share, centred=False, ease=None, real=None):
        self.own, self.quiet, self.w1aw, self.own_share, self.w1aw_share = own, quiet, w1aw, own_share, w1aw_share
        self.centred = centred
        self.ease = ease    # --scratch: how easy the practice sound is now (shared with the main process)
        self.real = real    # --scratch: the part of the real shares in use now, 0..1

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        wid = info.id if info else 0
        rng = np.random.default_rng([4243, wid, int(time.time() * 1000) % 1000000])
        torch.set_num_threads(1)
        gen.load()
        while True:
            u = rng.random()
            cen = None
            f = 1.0 if self.real is None else self.real.value
            ease = 0.0 if self.ease is None else self.ease.value
            if self.w1aw is not None and u < self.w1aw_share * f:
                wt = self.w1aw.windows['train']
                i = wt[rng.integers(len(wt))]
                x, txt, ev = self.w1aw.sample(i)
                if self.centred:
                    cen = centres_for(self.w1aw.meta[i]['note'], rng)
                if rng.random() < 0.3:
                    x = x + rng.uniform(0.5, 3.0) * gen.noise_for(rng, N)
            elif u < (self.w1aw_share + self.own_share) * f:
                if self.quiet.windows['train'] and rng.random() < 0.08:
                    q = self.quiet.windows['train']
                    i = q[rng.integers(len(q))]
                    x, txt, ev = self.quiet.sample(i)
                    if self.centred:
                        cen = centres_for(rng.uniform(320, 1080), rng)
                else:
                    wt = self.own.windows['train']
                    i = wt[rng.integers(len(wt))]
                    x, txt, ev = self.own.sample(i)
                    if self.centred:
                        cen = centres_for(self.own.meta[i]['note'], rng)
                x = x / (x.std() + 1e-9)
                if rng.random() < 0.5:
                    x = x + rng.uniform(0.5, 4.0) * gen.noise_for(rng, N)
            elif self.centred:
                x, txt, ev, tr = gen.make_sample(rng, SEC, silent_prob=0.04, ease=ease, with_track=True)
                cen = centre_bins(tr, FRAMES)
            else:
                x, txt, ev = gen.make_sample(rng, SEC, silent_prob=0.04, ease=ease)
            if self.centred:
                yield x.astype(np.float32), gen.encode(txt), frame_targets(ev), cen
            else:
                yield x.astype(np.float32), gen.encode(txt), frame_targets(ev)


def evaluate(net, cut, dev, n=200, centred=False):
    hold = cut.windows['hold']
    if not hold:
        return None
    net.eval()
    pick = [hold[i] for i in np.linspace(0, len(hold) - 1, min(n, len(hold))).astype(int)]
    errs = chars = 0
    with torch.no_grad():
        for i in range(0, len(pick), 32):
            batch = [cut.sample(w) for w in pick[i:i + 32]]
            audio = torch.from_numpy(np.stack([b[0] for b in batch])).to(dev)
            if centred:
                cen = torch.from_numpy(np.stack([centre_bins(cut.meta[w]['note'], FRAMES) for w in pick[i:i + 32]]))
                x = spectrogram_centred_torch(audio, cen)
            else:
                x = spectrogram_torch(audio)
            with torch.autocast(dev.type, dtype=torch.float16, enabled=dev.type == 'cuda'):
                lp = net(x)
            lp = lp.float().cpu()
            for j, (_, ref, _) in enumerate(batch):
                errs += edit(ref, greedy(lp[j])); chars += max(1, len(ref))
    net.train()
    return errs / max(1, chars)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--own', required=True)
    ap.add_argument('--w1aw', default=None)
    ap.add_argument('--init', default=None)
    ap.add_argument('--scratch', default=None, choices=list(SIZES))
    ap.add_argument('--out', required=True)
    ap.add_argument('--steps', type=int, default=2000)
    ap.add_argument('--batch', type=int, default=32)
    ap.add_argument('--lr', type=float, default=1e-4)
    ap.add_argument('--workers', type=int, default=max(1, (os.cpu_count() or 2)))
    ap.add_argument('--own-share', type=float, default=0.25)
    ap.add_argument('--w1aw-share', type=float, default=0.25)
    ap.add_argument('--w1aw-hold', default='ARLP042,ARLD041,ARLD038,ARLD039')
    ap.add_argument('--centred', action='store_true')
    a = ap.parse_args()
    if bool(a.init) == bool(a.scratch):
        ap.error('give either --init or --scratch')
    if a.scratch and a.lr == 1e-4:
        a.lr = 1e-3   # from zero, as train_gpu.py
    os.makedirs(a.out, exist_ok=True)
    dev = torch.device('cuda' if torch.cuda.is_available() else 'cpu')

    own = Cut(a.own, lambda m: m['kind'] == 'train', lambda m: m['kind'] == 'hold')
    quiet = Cut(a.own, lambda m: m['kind'] == 'quiet', lambda m: False)
    hold = set(a.w1aw_hold.split(','))
    w1 = Cut(a.w1aw, lambda m: not m.get('drop') and m['code'] not in hold,
             lambda m: not m.get('drop') and m['code'] in hold and not m['rx'].startswith('F_')) if a.w1aw else None

    if a.scratch:
        size = a.scratch
        net = Net(**SIZES[size]).to(dev)
    else:
        z = torch.load(a.init, map_location='cpu', weights_only=False)
        size = z.get('size', 'base')
        net = Net(**SIZES[size]).to(dev)
        net.load_state_dict(z['net'])
    opt = torch.optim.AdamW(net.parameters(), lr=a.lr, weight_decay=0.01)
    scaler = torch.amp.GradScaler(enabled=dev.type == 'cuda')
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=True, reduction='none')
    wts = torch.ones(NCLASS); wts[:BLANK] = 6.0
    ce = nn.CrossEntropyLoss(weight=wts.to(dev))
    log = open(os.path.join(a.out, 'train.log'), 'a')

    def say(s):
        print(s, flush=True); log.write(s + '\n'); log.flush()

    def score():
        o = evaluate(net, own, dev, centred=a.centred)
        w = evaluate(net, w1, dev, centred=a.centred) if w1 else None
        return (o if w is None else 0.5 * (o + w)), o, w

    step, best = 0, None
    last = os.path.join(a.out, 'ckpt_last.pt')
    if os.path.exists(last):
        r = torch.load(last, map_location=dev, weights_only=False)
        net.load_state_dict(r['net']); opt.load_state_dict(r['opt']); step = r['step']; best = r['best']
        if 'scaler' in r and dev.type == 'cuda':
            scaler.load_state_dict(r['scaler'])
    say("device %s, params %d, his windows %d to teach / %d held out / %d empty band, W1AW %s, from step %d of %d" % (
        dev, n_params(net), len(own.windows['train']), len(own.windows['hold']), len(quiet.windows['train']),
        '-' if not w1 else '%d to teach / %d held out' % (len(w1.windows['train']), len(w1.windows['hold'])), step, a.steps))
    if best is None:
        best, o, w = score()
        say("  HELD-OUT REAL before any step: own sessions %.3f, W1AW %s" % (o, '-' if w is None else '%.3f' % w))
        torch.save({'net': net.state_dict(), 'step': 0, 'cer': best, 'size': size}, os.path.join(a.out, 'ckpt_best.pt'))

    ease = mp.Value('d', 14.0 if a.scratch else 0.0)
    real = mp.Value('d', 0.0 if a.scratch else 1.0)
    loader = torch.utils.data.DataLoader(Mixed(own, quiet, w1, a.own_share, a.w1aw_share, a.centred,
                                               ease if a.scratch else None, real if a.scratch else None), batch_size=a.batch,
                                         num_workers=a.workers, collate_fn=collate_c if a.centred else collate,
                                         prefetch_factor=4, persistent_workers=True)
    t0, step0 = time.time(), step
    for batch in loader:
        audio, y, lens, ft = batch[:4]
        if step >= a.steps:
            break
        if a.scratch:
            lr = a.lr * min(1.0, (step + 1) / 1000) * (0.05 + 0.95 * 0.5 * (1 + np.cos(np.pi * min(1.0, step / a.steps))))
            ease.value = 14.0 if step < 600 else max(0.0, 14.0 * (2400 - step) / 1800)
            real.value = min(1.0, step / 3000.0)
        else:
            lr = a.lr * min(1.0, (step + 1) / 100) * (0.1 + 0.9 * 0.5 * (1 + np.cos(np.pi * step / a.steps)))
        for g in opt.param_groups:
            g['lr'] = lr
        with torch.no_grad():
            if a.centred:
                x = spectrogram_centred_torch(audio.to(dev, non_blocking=True), batch[4])
            else:
                x = spectrogram_torch(audio.to(dev, non_blocking=True))
        with torch.autocast(dev.type, dtype=torch.float16, enabled=dev.type == 'cuda'):
            lp, fl = net(x)
        lp, fl = lp.float(), fl.float()
        inl = torch.full((lp.shape[0],), lp.shape[1], dtype=torch.long)
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
        if step % 25 == 0:
            say("step %5d  ctc %.2f frame %.3f  lr %.1e  %.2f s/step" % (step, float(l_ctc), float(l_frame), lr, (time.time() - t0) / (step - step0)))
        if step % 250 == 0 or step == a.steps:
            cer, o, w = score()
            say("  HELD-OUT REAL step %d: own sessions %.3f, W1AW %s" % (step, o, '-' if w is None else '%.3f' % w))
            if cer < best:
                best = cer
                torch.save({'net': net.state_dict(), 'step': step, 'cer': cer, 'size': size}, os.path.join(a.out, 'ckpt_best.pt'))
            torch.save({'net': net.state_dict(), 'opt': opt.state_dict(), 'scaler': scaler.state_dict(), 'step': step,
                        'best': best, 'size': size}, last + '.tmp')
            os.replace(last + '.tmp', last)


if __name__ == '__main__':
    mp.freeze_support()
    main()
