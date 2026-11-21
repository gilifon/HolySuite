"""
THE SMALL NETWORK: a convolutional front end over the spectrogram, a few conformer blocks (feed-forward,
self-attention over the whole window, depthwise convolution), and a CTC head. The window is looked at as one
piece - that is what lets a later letter help read an earlier one, and the start of a new station.
"""
import math
import torch
import torch.nn as nn
import torch.nn.functional as F

from common import NBINS, NCLASS


class FF(nn.Module):
    def __init__(self, d, mult, drop):
        super().__init__()
        self.net = nn.Sequential(nn.LayerNorm(d), nn.Linear(d, d * mult), nn.SiLU(), nn.Dropout(drop), nn.Linear(d * mult, d), nn.Dropout(drop))

    def forward(self, x):
        return self.net(x)


class ConvModule(nn.Module):
    def __init__(self, d, k, drop):
        super().__init__()
        self.ln = nn.LayerNorm(d)
        self.pw1 = nn.Linear(d, 2 * d)
        self.dw = nn.Conv1d(d, d, k, padding=k // 2, groups=d)
        self.gn = nn.GroupNorm(1, d)
        self.pw2 = nn.Linear(d, d)
        self.drop = nn.Dropout(drop)

    def forward(self, x):
        y = F.glu(self.pw1(self.ln(x)), dim=-1)
        y = self.dw(y.transpose(1, 2))
        y = F.silu(self.gn(y)).transpose(1, 2)
        return self.drop(self.pw2(y))


class Block(nn.Module):
    def __init__(self, d, heads, ff, k, drop):
        super().__init__()
        self.ff1 = FF(d, ff, drop)
        self.ln_att = nn.LayerNorm(d)
        self.att = nn.MultiheadAttention(d, heads, dropout=drop, batch_first=True)
        self.conv = ConvModule(d, k, drop)
        self.ff2 = FF(d, ff, drop)
        self.ln = nn.LayerNorm(d)

    def forward(self, x):
        x = x + 0.5 * self.ff1(x)
        y = self.ln_att(x)
        x = x + self.att(y, y, y, need_weights=False)[0]
        x = x + self.conv(x)
        x = x + 0.5 * self.ff2(x)
        return self.ln(x)


class Net(nn.Module):
    def __init__(self, d=96, heads=4, ff=3, blocks=3, k=15, drop=0.1, ch=(8, 16, 16)):
        super().__init__()
        c1, c2, c3 = ch
        self.front = nn.Sequential(
            nn.Conv2d(1, c1, 3, padding=1), nn.GELU(),
            nn.Conv2d(c1, c2, 3, stride=(2, 2), padding=1), nn.GELU(),
            nn.Conv2d(c2, c3, 3, stride=(2, 2), padding=1), nn.GELU())
        fbins = (((NBINS + 1) // 2) + 1) // 2
        self.proj = nn.Linear(c3 * fbins, d)
        self.blocks = nn.ModuleList([Block(d, heads, ff, k, drop) for _ in range(blocks)])
        self.head = nn.Linear(d, NCLASS)
        self.frame_head = nn.Linear(d, NCLASS)      # which letter is said at this step (none = blank class), for dense training
        self.d = d
        self.want_frames = False

    def pos(self, n, device):
        p = torch.arange(n, device=device).float()[:, None]
        i = torch.arange(0, self.d, 2, device=device).float()[None, :]
        ang = p / (10000 ** (i / self.d))
        pe = torch.zeros(n, self.d, device=device)
        pe[:, 0::2] = torch.sin(ang); pe[:, 1::2] = torch.cos(ang)
        return pe

    def forward(self, x):
        """x [B, T, NBINS] -> log-probs [B, T', NCLASS], T' = about T/4"""
        y = self.front(x.unsqueeze(1))                         # [B, C, T/4, F/4]
        b, c, t, f = y.shape
        y = self.proj(y.permute(0, 2, 1, 3).reshape(b, t, c * f))
        y = y + 0.3 * self.pos(t, y.device)
        for blk in self.blocks:
            y = blk(y)
        if self.training or self.want_frames:
            return F.log_softmax(self.head(y), dim=-1), self.frame_head(y)
        return F.log_softmax(self.head(y), dim=-1)


def n_params(m):
    return sum(p.numel() for p in m.parameters())


if __name__ == '__main__':
    import time
    m = Net()
    print("params", n_params(m))
    x = torch.randn(24, 1001, NBINS)
    t0 = time.time()
    for _ in range(3):
        y = m(x); y.sum().backward()
    print("out", tuple(y.shape), "%.2f s per step of 24" % ((time.time() - t0) / 3))
