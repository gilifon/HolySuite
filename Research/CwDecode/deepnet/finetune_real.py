"""
TEACHING THE NETWORK ON REAL ON-AIR SOUND (2026-10-17) - his station keyed by FistKeyer, heard by European KiwiSDRs.

Why: the second Colab run, all generated practice, made the network WORSE on real recordings (it fell silent on
weak, fading signals) while its practice score improved. So this goes on from the network in HolyLogger now and
shows it the real thing: every batch is half real windows, half generated practice (so it does not forget what
it knew), at a low learning rate.

THE REAL WINDOWS. realdata.align finds where FistKeyer's clock sits in every receiver's piece. A window is 15 s
that STARTS and ENDS in a gap between letters - so no letter is cut in half and the label is exactly the letters
inside it, a space wherever a word ended. Pieces where the receiver did not hear him are left out, and so is any
window where the tone stands less than MIN_DB above the gaps beside it (a label for letters nobody could hear
teaches inventing). Half of the real windows get extra real band noise laid on, which makes weak copies out of
strong ones.

HELD OUT: the last HOLD_PARTS parts of the session, on every receiver. They are only ever scored, never taught -
and the four benches are not touched at all.

  finetune_real.py <session dir> [<session dir> ...] --init ckpt_colab40k.pt --out DIR [--steps 1500] [--batch 32] [--lr 1e-4]
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
import realdata
from model import Net, n_params
from train_gpu import SIZES, SEC, TOK, frame_targets, collate, edit, greedy

# Overridable (2026-10-28): the 5 W session's weakest receivers (DF3AF 2-5 dB, G4AHG 1-3) match at 0.1-0.25 -
# over 300 s that is far above chance - and REAL_MIN_CORR/REAL_MIN_DB let a run keep them to see if they help.
MIN_CORR = float(os.environ.get('REAL_MIN_CORR', 0.25))  # a piece whose keying matched worse than this was not heard
MIN_DB = float(os.environ.get('REAL_MIN_DB', 3.0))       # a window whose tone stands less than this above the gaps is left out
HOLD_PARTS = 4
N = int(SEC * RATE)


def pieces(session):
    """Aligned pieces, cached in <session>/align.json (aligning takes minutes)."""
    cache = os.path.join(session, 'align.json')
    if os.path.exists(cache):
        return json.load(open(cache))
    out = realdata.align(session)
    json.dump(out, open(cache + '.tmp', 'w'), indent=1, default=float)
    os.replace(cache + '.tmp', cache)
    return out


def letters(edges):
    """-> one row per letter: (part, text_index, char, first down, last up) in keyer seconds."""
    by = {}
    for p, ti, c, d, u in edges:
        k = (p, ti)
        if k not in by:
            by[k] = [p, ti, c, d, u]
        else:
            by[k][3] = min(by[k][3], d)
            by[k][4] = max(by[k][4], u)
    return [tuple(v) for _, v in sorted(by.items(), key=lambda kv: kv[1][3])]


def word_starts(texts, lets):
    """True for each letter that begins a word (after a space in its part's text, or the first of a part)."""
    out = []
    for p, ti, c, d, u in lets:
        out.append(ti == 0 or texts[p][ti - 1] == ' ')
    return out


class Session:
    def __init__(self, session):
        edges, self.texts = realdata.read_edges(session)
        self.lets = letters(edges)
        self.starts = word_starts(self.texts, self.lets)
        self.down = np.array([l[3] for l in self.lets])
        self.up = np.array([l[4] for l in self.lets])
        self.part = np.array([l[0] for l in self.lets])
        self.K = realdata.keyer_frames(edges)
        self.audio = {}
        self.windows = {'train': [], 'hold': [], 'quiet': []}
        k_first, k_last = float(self.down.min()), float(self.up.max())
        hold_from = max(self.part) - HOLD_PARTS + 1
        for r in pieces(session):
            if r['score'] < MIN_CORR:
                # a piece wholly before his first or after his last element is still real empty band
                a = realdata.load(r['path'])
                n0 = len(self.windows['quiet'])
                self._quiet(r, a, len(a) / RATE, k_first, k_last)
                if len(self.windows['quiet']) > n0:
                    self.audio[r['path']] = a
                continue
            a = realdata.load(r['path'])
            self.audio[r['path']] = a
            mag = realdata.magnitude(a)[:, r['bin']]
            dur = len(a) / RATE
            # every gap between two letters is a possible start or end
            gap_mid = r['t0'] + 0.5 * (self.up[:-1] + self.down[1:])
            ok_gap = (self.down[1:] - self.up[:-1]) > 0.08
            mids = np.sort(gap_mid[ok_gap])
            env = realdata.envelope(edges, r['t0'], len(mag))
            self._quiet(r, a, dur, k_first, k_last)
            for s in mids:
                if s < 0 or s + SEC > dur:
                    continue
                e = s + SEC
                j = np.searchsorted(mids, e)
                # the end must be in a gap too: the nearest gap middle within 60 ms of s+15
                near = [m for m in mids[max(0, j - 1):j + 1] if abs(m - e) < 0.06]
                if not near:
                    continue
                inside = np.nonzero((r['t0'] + self.down >= s) & (r['t0'] + self.up <= s + SEC))[0]
                if len(inside) < 4:
                    continue
                i0, i1 = int(s / realdata.FR), int((s + SEC) / realdata.FR)
                on, off = mag[i0:i1][env[i0:i1] > 0], mag[i0:i1][env[i0:i1] == 0]
                if len(on) < 20 or len(off) < 20:
                    continue
                db = 20 * np.log10(np.median(on) / (np.median(off) + 1e-9))
                if db < MIN_DB:
                    continue
                kind = 'hold' if self.part[inside].min() >= hold_from else ('train' if self.part[inside].max() < hold_from else None)
                if kind:
                    self.windows[kind].append((r['path'], float(s), r['t0'], float(db)))

    def _quiet(self, r, a, dur, k_first, k_last):
        """REAL EMPTY BAND (2026-10-18): 15 s of this receiver from before his first element or after his last,
        labelled with nothing - the network invented 42 letters on the IC-7610 bench's two empty recordings. A
        window where any note rises well above itself is NOT used: that is another station, and teaching it
        'say nothing' over real CW would teach it to ignore real CW."""
        lo, hi = r['t0'] + k_first - 2.0, r['t0'] + k_last + 2.0
        for s in np.arange(0.5, dur - SEC - 0.1, 7.5):
            if not (s + SEC < lo or s > hi):
                continue
            seg = a[int(s * RATE):int(s * RATE) + N]
            m = 20 * np.log10(realdata.magnitude(seg) + 1e-6)
            lift = np.percentile(m, 95, axis=0) - np.percentile(m, 30, axis=0)
            if lift.max() < 14.0:
                self.windows['quiet'].append((r['path'], float(s), None, 0.0))

    def sample(self, w):
        path, s, t0, _ = w
        a = self.audio[path]
        i = int(round(s * RATE))
        x = a[i:i + N].astype(np.float32)
        if len(x) < N:
            x = np.pad(x, (0, N - len(x)))
        if t0 is None:
            return x, '', []
        inside = np.nonzero((t0 + self.down >= s) & (t0 + self.up <= s + SEC))[0]
        txt, ev = [], []
        for n, k in enumerate(inside):
            if n > 0 and self.starts[k]:
                txt.append(' ')
                ev.append((0.5 * (t0 + self.up[inside[n - 1]] + t0 + self.down[k]) - s, ' '))
            c = self.lets[k][2]
            txt.append(c)
            ev.append((t0 + self.up[k] + 0.05 - s, c))
        return x, ''.join(txt), ev


class Pool:
    """Several sessions as one: windows are (session number, window)."""
    def __init__(self, dirs):
        self.sessions = [Session(d) for d in dirs]
        self.windows = {k: [(i, w) for i, ss in enumerate(self.sessions) for w in ss.windows[k]] for k in ('train', 'hold', 'quiet')}

    def sample(self, iw):
        return self.sessions[iw[0]].sample(iw[1])


class W1aw:
    """W1AW's bulletin heard in Europe (w1awdata.py): cut windows in <prefix>.npy, labels in <prefix>.json. Nights
    whose bulletin is in `hold` are only scored - W1AW repeats a bulletin for days, so holding out NIGHTS would
    hold out nothing; holding out a BULLETIN keeps its words unseen."""
    def __init__(self, prefix, hold):
        self.prefix = prefix
        self.meta = json.load(open(prefix + '.json'))
        self.audio = None
        # 'drop': strong on its own note yet unreadable to the network that labelled it - another station on top
        keep = [i for i, m in enumerate(self.meta) if not m.get('drop')]
        # The held-out score uses only the OTHER receivers: the strong copy's labels came from the network's own
        # reading (checked against the published text), so it scores ~0 there by construction (1.5% on 2026-10-19).
        # The strong copy was the French receiver on every night.
        self.windows = {'train': [i for i in keep if self.meta[i]['code'] not in hold],
                        'hold': [i for i in keep if self.meta[i]['code'] in hold and not self.meta[i]['rx'].startswith('F_')]}

    def sample(self, i):
        if self.audio is None:
            self.audio = np.load(self.prefix + '.npy', mmap_mode='r')
        m = self.meta[i]
        return np.asarray(self.audio[i], dtype=np.float32), m['label'], [(t, c) for t, c in m['events']]


class Mixed(torch.utils.data.IterableDataset):
    """Generated practice, his own on-air sessions and (if given) W1AW, in the shares asked for."""
    def __init__(self, sess, w1aw=None, own_share=0.5, w1aw_share=0.0):
        self.sess, self.w1aw, self.own_share, self.w1aw_share = sess, w1aw, own_share, w1aw_share

    def __iter__(self):
        info = torch.utils.data.get_worker_info()
        wid = info.id if info else 0
        rng = np.random.default_rng([4242, wid, int(time.time() * 1000) % 1000000])
        torch.set_num_threads(1)
        gen.load()
        win = self.sess.windows['train']
        quiet = self.sess.windows['quiet']
        while True:
            u = rng.random()
            if self.w1aw is not None and u < self.w1aw_share:
                wt = self.w1aw.windows['train']
                x, txt, ev = self.w1aw.sample(wt[rng.integers(len(wt))])
                if rng.random() < 0.3:                       # weaker still
                    x = x + rng.uniform(0.5, 3.0) * gen.noise_for(rng, N)
            elif u < self.w1aw_share + self.own_share:
                if quiet and rng.random() < 0.08:
                    x, txt, ev = self.sess.sample(quiet[rng.integers(len(quiet))])
                else:
                    x, txt, ev = self.sess.sample(win[rng.integers(len(win))])
                x = x / (x.std() + 1e-9)
                if rng.random() < 0.5:                       # a weaker copy: more real band noise on top
                    x = x + rng.uniform(0.5, 4.0) * gen.noise_for(rng, N)
            else:
                # run 1's share of empty samples (4%), not run 2's 12%: run 2 taught it to fall silent
                x, txt, ev = gen.make_sample(rng, SEC, silent_prob=0.04)
            yield x.astype(np.float32), gen.encode(txt), frame_targets(ev)


def evaluate(net, sess, dev, n=200):
    """Character error rate on held-out real windows."""
    net.eval()
    hold = sess.windows['hold']
    pick = [hold[i] for i in np.linspace(0, len(hold) - 1, min(n, len(hold))).astype(int)]
    errs = chars = 0
    with torch.no_grad():
        for i in range(0, len(pick), 16):
            batch = [sess.sample(w) for w in pick[i:i + 16]]
            x = spectrogram_torch(torch.from_numpy(np.stack([b[0] for b in batch])).to(dev))
            lp = net(x)
            for j, (_, ref, _) in enumerate(batch):
                errs += edit(ref, greedy(lp[j].float().cpu())); chars += max(1, len(ref))
    net.train()
    return errs / max(1, chars)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('session', nargs='+')
    ap.add_argument('--init', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--steps', type=int, default=1500)
    ap.add_argument('--batch', type=int, default=32)
    ap.add_argument('--lr', type=float, default=1e-4)
    ap.add_argument('--workers', type=int, default=5)
    ap.add_argument('--count', action='store_true', help='only count the windows and exit')
    ap.add_argument('--w1aw', default=None, help='prefix of w1awdata.py cut output')
    ap.add_argument('--w1aw-hold', default='', help='bulletins only scored, e.g. ARLP042,ARLD041')
    ap.add_argument('--own-share', type=float, default=0.5)
    ap.add_argument('--w1aw-share', type=float, default=0.0)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    sess = Pool(a.session)
    w1 = W1aw(a.w1aw, set(a.w1aw_hold.split(','))) if a.w1aw else None
    if w1:
        print("W1AW windows: %d to teach, %d held out" % (len(w1.windows['train']), len(w1.windows['hold'])), flush=True)
    print("real windows: %d to teach, %d held out, %d of empty band" % (len(sess.windows['train']), len(sess.windows['hold']), len(sess.windows['quiet'])), flush=True)
    if a.count:
        for k in ('train', 'hold', 'quiet'):
            per = {}
            for _, (p, s, t0, db) in sess.windows[k]:
                per.setdefault(os.path.basename(p).split('_')[0], []).append(db)
            print(k, {r: (len(v), round(float(np.median(v)), 1)) for r, v in sorted(per.items())})
        x, t, ev = sess.sample(sess.windows['train'][len(sess.windows['train']) // 2])
        print("example label:", t)
        return
    dev = torch.device('cpu')
    torch.set_num_threads(3)
    z = torch.load(a.init, map_location=dev, weights_only=False)
    net = Net(**SIZES[z.get('size', 'base')])
    net.load_state_dict(z['net'])
    opt = torch.optim.AdamW(net.parameters(), lr=a.lr, weight_decay=0.01)
    ctc = nn.CTCLoss(blank=BLANK, zero_infinity=True, reduction='none')
    wts = torch.ones(NCLASS); wts[:BLANK] = 6.0
    ce = nn.CrossEntropyLoss(weight=wts)
    log = open(os.path.join(a.out, 'train.log'), 'a')

    def say(s):
        print(s, flush=True); log.write(s + '\n'); log.flush()

    def score():
        """Held-out error on his sessions and, if given, on W1AW's held-out bulletin; the best network is the one
        with the lowest MEAN, so neither kind of sound can be traded away for the other."""
        own = evaluate(net, sess, dev)
        w = evaluate(net, w1, dev) if w1 and w1.windows['hold'] else None
        return (own if w is None else 0.5 * (own + w)), own, w

    best, own0, w0 = score()
    say("params %d; held-out real CER before any step: own sessions %.3f, W1AW %s" % (n_params(net), own0, '-' if w0 is None else '%.3f' % w0))
    torch.save({'net': net.state_dict(), 'step': 0, 'cer': best, 'size': z.get('size', 'base')}, os.path.join(a.out, 'ckpt_best.pt'))
    loader = torch.utils.data.DataLoader(Mixed(sess, w1, a.own_share, a.w1aw_share), batch_size=a.batch, num_workers=a.workers, collate_fn=collate,
                                         prefetch_factor=2, persistent_workers=True)
    t0 = time.time()
    step = 0
    for audio, y, lens, ft in loader:
        if step >= a.steps:
            break
        lr = a.lr * min(1.0, (step + 1) / 100) * (0.1 + 0.9 * 0.5 * (1 + np.cos(np.pi * step / a.steps)))
        for g in opt.param_groups:
            g['lr'] = lr
        with torch.no_grad():
            x = spectrogram_torch(audio)
        lp, fl = net(x)
        inl = torch.full((lp.shape[0],), lp.shape[1], dtype=torch.long)
        l_ctc = (ctc(lp.transpose(0, 1), y, inl, lens) / lens.clamp(min=8).float()).mean()
        l_frame = ce(fl.reshape(-1, NCLASS), ft[:, :fl.shape[1]].reshape(-1))
        loss = l_ctc + l_frame
        opt.zero_grad(set_to_none=True)
        loss.backward()
        nn.utils.clip_grad_norm_(net.parameters(), 1.0)
        opt.step()
        step += 1
        if step % 25 == 0:
            say("step %5d  ctc %.2f frame %.3f  lr %.1e  %.1f s/step" % (step, float(l_ctc), float(l_frame), lr, (time.time() - t0) / step))
        if step % 250 == 0 or step == a.steps:
            cer, own_c, w_c = score()
            say("  HELD-OUT REAL step %d: own sessions %.3f, W1AW %s" % (step, own_c, '-' if w_c is None else '%.3f' % w_c))
            torch.save({'net': net.state_dict(), 'step': step, 'cer': cer, 'size': z.get('size', 'base')}, os.path.join(a.out, 'ckpt_last.pt'))
            if cer < best:
                best = cer
                torch.save({'net': net.state_dict(), 'step': step, 'cer': cer, 'size': z.get('size', 'base')}, os.path.join(a.out, 'ckpt_best.pt'))


if __name__ == '__main__':
    mp.freeze_support()
    main()
