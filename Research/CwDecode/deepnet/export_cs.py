"""
EXPORT THE TRAINED NETWORK FOR HOLYLOGGER (a plain binary of float32, read by CwDeepNet.cs - no ONNX, no library).

  export_cs.py <checkpoint.pt> <out CwDeep.bin> [reference prefix] [--centred]

--centred (2026-10-19): the network reads frames centred on the followed note (common.spectrogram_centred); the file
then starts "CWD2" instead of "CWD1", so HolyLogger knows to feed it that way.

File layout (little endian):
  "CWD1"                                   4 bytes
  int32 x 11:  d, heads, ff, blocks, k, c1, c2, c3, nbins, nclass, fbins   (fbins = frequency steps after the front end)
  then float32 tensors, in this order (row-major, exactly as PyTorch stores them):
    front.0.weight [c1,1,3,3]  front.0.bias [c1]
    front.2.weight [c2,c1,3,3] front.2.bias [c2]
    front.4.weight [c3,c2,3,3] front.4.bias [c3]
    proj.weight [d, c3*fbins]  proj.bias [d]
    per block: ff1.net.0.weight/bias (LayerNorm) ff1.net.1.weight/bias (Linear d->d*ff) ff1.net.4.weight/bias (Linear d*ff->d)
               ln_att.weight/bias  att.in_proj_weight [3d,d]  att.in_proj_bias [3d]  att.out_proj.weight [d,d]  att.out_proj.bias [d]
               conv.ln.weight/bias  conv.pw1.weight [2d,d] bias  conv.dw.weight [d,1,k] bias  conv.gn.weight/bias [d]
               conv.pw2.weight [d,d] bias
               ff2.* as ff1
               ln.weight/bias
    head.weight [nclass,d]  head.bias [nclass]

With a reference prefix it also writes <prefix>_in.bin (a [T,nbins] spectrogram of a real recording) and
<prefix>_out.bin ([T',nclass] log-probabilities from PyTorch) so the C# forward pass can be checked number for number.
"""
import os
import struct
import sys
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *
from model import Net
from train_gpu import SIZES


def main():
    centred = '--centred' in sys.argv
    args = [x for x in sys.argv[1:] if x != '--centred']
    ck, out = args[0], args[1]
    z = torch.load(ck, map_location='cpu', weights_only=False)
    cfg = SIZES[z['size']] if 'size' in z else dict(d=96, heads=4, ff=3, blocks=3, ch=(8, 16, 16))
    net = Net(**cfg)
    net.load_state_dict(z['net'])
    net.eval()
    sd = net.state_dict()
    d, heads, ff, blocks = cfg['d'], cfg['heads'], cfg['ff'], cfg['blocks']
    c1, c2, c3 = cfg['ch']
    fbins = (((NBINS + 1) // 2) + 1) // 2
    names = ['front.0.weight', 'front.0.bias', 'front.2.weight', 'front.2.bias', 'front.4.weight', 'front.4.bias', 'proj.weight', 'proj.bias']
    for b in range(blocks):
        p = 'blocks.%d.' % b
        for part in ('ff1.net.0', 'ff1.net.1', 'ff1.net.4'):
            names += [p + part + '.weight', p + part + '.bias']
        names += [p + 'ln_att.weight', p + 'ln_att.bias', p + 'att.in_proj_weight', p + 'att.in_proj_bias',
                  p + 'att.out_proj.weight', p + 'att.out_proj.bias',
                  p + 'conv.ln.weight', p + 'conv.ln.bias', p + 'conv.pw1.weight', p + 'conv.pw1.bias',
                  p + 'conv.dw.weight', p + 'conv.dw.bias', p + 'conv.gn.weight', p + 'conv.gn.bias',
                  p + 'conv.pw2.weight', p + 'conv.pw2.bias']
        for part in ('ff2.net.0', 'ff2.net.1', 'ff2.net.4'):
            names += [p + part + '.weight', p + part + '.bias']
        names += [p + 'ln.weight', p + 'ln.bias']
    names += ['head.weight', 'head.bias']
    with open(out, 'wb') as f:
        f.write(b'CWD2' if centred else b'CWD1')
        f.write(struct.pack('<11i', d, heads, ff, blocks, cfg.get('k', 15), c1, c2, c3, NBINS, NCLASS, fbins))
        total = 0
        for n in names:
            t = sd[n].detach().cpu().numpy().astype('<f4')
            f.write(t.tobytes())
            total += t.size
    print("wrote", out, total, "numbers,", os.path.getsize(out) // 1024, "KB")
    if len(args) > 2:
        prefix = args[2]
        import wave
        from math import gcd
        from scipy.signal import resample_poly
        path = os.path.join(HERE, '..', 'recordings', 'weak.wav')
        with wave.open(path) as w:
            rate = w.getframerate()
            x = np.frombuffer(w.readframes(w.getnframes()), dtype='<i2').astype(np.float32) / 32768.0
        g = gcd(rate, RATE)
        a = resample_poly(x, RATE // g, rate // g).astype(np.float32)[:15 * RATE]
        feats = spectrogram_centred(a, 500.0) if centred else spectrogram(a)
        with torch.no_grad():
            lp = net(torch.from_numpy(feats)[None])[0].numpy()
        feats.astype('<f4').tofile(prefix + '_in.bin')
        lp.astype('<f4').tofile(prefix + '_out.bin')
        a.astype('<f4').tofile(prefix + '_audio.bin')
        print("reference:", feats.shape, "->", lp.shape)


if __name__ == '__main__':
    main()
