"""
OUR NETWORK AS A DECODER: same interface as deepcw/DeepCwBench.py (read_wav, to_model_rate, decode), so the
same bench scripts score both. A long recording is cut into 15 s windows every 10 s; from each window only the
letters in its middle are kept (the first window its start, the last its end).

  CKPT=best|last   which checkpoint (default best)
"""
import os
import sys
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, '..', 'deepcw'))
from common import *
from model import Net
import DeepCwBench as _D
from DeepCwBench import read_wav

_last_path = None


def read_wav(path):
    """As DeepCwBench's, remembering the file: the centred network (CENTRED=1) reads Plain's note from <wav>.track."""
    global _last_path
    _last_path = path
    return _D.read_wav(path)


def plain_track():
    """(seconds, Hz) arrays from tools/PlainTrack's file for the last WAV read; None if there is none. A block with no
    signal keeps the last note Plain had (as the window in HolyLogger does); before the first, Plain's first note."""
    if not _last_path or not os.path.exists(_last_path + '.track'):
        return None
    t, hz = np.loadtxt(_last_path + '.track', unpack=True, ndmin=2)
    good = hz > 0
    if not good.any():
        return t, np.full_like(hz, 700.0)
    last = hz[good][0]
    out = hz.copy()
    for i in range(len(out)):
        if out[i] > 0:
            last = out[i]
        else:
            out[i] = last
    return t, out


def track_for(start_s, frames):
    tr = plain_track()
    if tr is None:
        return 700.0
    t = start_s + np.arange(frames) * HOP / RATE
    return np.interp(t, tr[0], tr[1])

WINDOW_S, STRIDE_S, KEEP_FROM, KEEP_TO = 15.0, 10.0, 2.5, 12.5
FRAME_S = 4 * HOP / RATE

_net = None


def _load(name):
    z = torch.load(os.path.join(HERE, 'ckpt_%s.pt' % name), map_location='cpu', weights_only=False)
    from train_gpu import SIZES
    m = Net(**SIZES[z["size"]]) if "size" in z else Net(); m.load_state_dict(z["net"]); m.eval()
    return m


def net():
    """CKPT=a+b+c (2026-10-28): several networks, their letter chances averaged frame by frame - all fine-tuned
    from the same start, so their frames line up."""
    global _net
    if _net is None:
        names = os.environ.get('CKPT', 'best').split('+')
        torch.set_num_threads(3)
        if len(names) == 1:
            _net = _load(names[0])
        else:
            nets = [_load(n) for n in names]
            def ensemble(x):
                lps = torch.stack([m(x) for m in nets])
                return torch.logsumexp(lps, 0) - np.log(len(nets))
            _net = ensemble
    return _net


def to_model_rate(x, rate):
    from math import gcd
    from scipy.signal import resample_poly
    if rate == RATE:
        return x
    g = gcd(rate, RATE)
    return resample_poly(x, RATE // g, rate // g).astype(np.float32)


def _cached_lp(feats, start_s):
    """The network's answer for a window; with LPCACHE=<folder> kept on disk per recording and window start, so
    a second run (another word list, another bonus) reads it back instead of running the network again."""
    folder = os.environ.get('LPCACHE')
    path = None
    if folder and _last_path:
        os.makedirs(folder, exist_ok=True)
        key = '%s_%s_%.3f' % (os.path.basename(os.path.dirname(_last_path)), os.path.basename(_last_path), start_s)
        path = os.path.join(folder, key + '.npy')
        if os.path.exists(path):
            return torch.from_numpy(np.load(path))
    with torch.no_grad():
        lp = net()(torch.from_numpy(feats)[None])[0]
    if path:
        np.save(path, lp.numpy())
    return lp


def decode_window(a, start_s=0.0):
    sl = os.environ.get('SLICE_HZ')
    if os.environ.get('CENTRED'):
        feats = spectrogram_centred(a, track_for(start_s, 1 + len(a) // HOP))
    else:
        feats = spectrogram_slice(a, float(sl)) if sl else spectrogram(a)
    lp = _cached_lp(feats, start_s)
    if os.environ.get('BEAM'):
        # the smarter reading (beam.py): a few spellings kept, known words rewarded
        from beam import beam_decode
        return [(ch, t * FRAME_S) for ch, t in beam_decode(lp.numpy(), beam=int(os.environ['BEAM']),
                                                            alpha=float(os.environ.get('ALPHA', '1.0')))]
    best = lp.argmax(-1).tolist()
    res, prev = [], None
    for t, k in enumerate(best):
        if k != BLANK and k != prev:
            res.append((CHARS[k], t * FRAME_S))
        prev = k
    return res


def decode_live(a, lag):
    """As HolyLogger's CwDeepDecoder reads: a 15 s window every 3 s, printing only letters at least `lag` s old."""
    n, stride = int(WINDOW_S * RATE), 3.0
    total = len(a) / RATE
    if len(a) < n:
        return ''.join(ch for ch, t in decode_window(a, 0.0))
    text, printed, end = [], None, WINDOW_S
    while True:
        last = end >= total
        e = min(end, total)
        s0 = int(e * RATE) - n
        hi = e if last else e - lag
        lo = s0 / RATE if printed is None else printed
        for ch, t in decode_window(a[s0:s0 + n], s0 / RATE):
            if lo <= s0 / RATE + t < hi:
                text.append(ch)
        printed = hi
        if last:
            break
        end += stride
    return ''.join(text)


def decode(a):
    if os.environ.get('LIVE_LAG'):
        return decode_live(a, float(os.environ['LIVE_LAG']))
    total = len(a) / RATE
    text = []
    start = 0.0
    while True:
        s0 = int(start * RATE); s1 = min(len(a), s0 + int(WINDOW_S * RATE))
        seg = a[s0:s1]
        if len(seg) < 4 * RATE:
            seg = a[max(0, len(a) - int(WINDOW_S * RATE)):]
            start = (len(a) - len(seg)) / RATE
        first = start == 0.0
        last = start + WINDOW_S >= total - 1e-6 or s1 >= len(a)
        lo = 0.0 if first else KEEP_FROM
        hi = 1e9 if last else KEEP_TO
        for ch, t in decode_window(seg, start):
            if lo <= t < hi:
                text.append(ch)
        if last:
            break
        start += STRIDE_S
    return ''.join(text)


def main():
    folder = sys.argv[1]
    only = sys.argv[2] if len(sys.argv) > 2 else None
    for f in sorted(os.listdir(folder)):
        if not f.lower().endswith('.wav'):
            continue
        name = os.path.splitext(f)[0]
        if only and only.lower() not in name.lower():
            continue
        rate, x = read_wav(os.path.join(folder, f))
        print(name + '\t' + decode(to_model_rate(x, rate)).replace('\n', ' '), flush=True)


if __name__ == '__main__':
    main()
