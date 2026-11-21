"""
OUR OWN NEURAL DECODER (started 2026-10-14) - shared definitions.

A small conformer-style network that reads a spectrogram of 300-1100 Hz and writes text by CTC, trained on
generated CW laid over REAL band noise taken from our own recordings. Everything about the input is fixed
here so the generator, the trainer and the decoder cannot drift apart.

Audio is 3200 Hz mono. Spectrogram: 256-point Hann FFT every 48 samples (15 ms), bins 24..88 (300-1100 Hz,
65 bins). Each note's magnitude is divided by that note's own noise floor over the window before log1p, so a recording's
gain, filter shape and noise level do not matter - only what stands out of its noise.
"""
import numpy as np

RATE = 3200
FFT = 256
HOP = 48
BIN0, BIN1 = 24, 89            # 300 Hz .. 1100 Hz, 65 bins
NBINS = BIN1 - BIN0
CHARS = list("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789,./?=+ ")     # = is BT, + is AR
BLANK = len(CHARS)
NCLASS = len(CHARS) + 1
C2I = {c: i for i, c in enumerate(CHARS)}
WINDOW = np.hanning(FFT + 1)[:-1].astype(np.float32)

MORSE = {
    'A': '.-', 'B': '-...', 'C': '-.-.', 'D': '-..', 'E': '.', 'F': '..-.', 'G': '--.', 'H': '....', 'I': '..',
    'J': '.---', 'K': '-.-', 'L': '.-..', 'M': '--', 'N': '-.', 'O': '---', 'P': '.--.', 'Q': '--.-', 'R': '.-.',
    'S': '...', 'T': '-', 'U': '..-', 'V': '...-', 'W': '.--', 'X': '-..-', 'Y': '-.--', 'Z': '--..',
    '0': '-----', '1': '.----', '2': '..---', '3': '...--', '4': '....-', '5': '.....', '6': '-....',
    '7': '--...', '8': '---..', '9': '----.', ',': '--..--', '.': '.-.-.-', '/': '-..-.', '?': '..--..',
    '=': '-...-', '+': '.-.-.',
}


def spectrogram(a):
    """float32 audio at RATE -> [T, NBINS] float32 features (log1p of magnitude / median magnitude)."""
    pad = FFT // 2
    a = np.pad(a, (pad, pad), mode='reflect')
    frames = 1 + (len(a) - FFT) // HOP
    idx = np.arange(FFT)[None, :] + HOP * np.arange(frames)[:, None]
    spec = np.abs(np.fft.rfft(a[idx] * WINDOW, n=FFT, axis=1))[:, BIN0:BIN1]
    # EACH NOTE'S OWN NOISE FLOOR (its 25th percentile over the window), not one median for the whole band:
    # his IC-7610's CW filter leaves the passband bright and everything outside it dead, and one global median
    # made a real recording's passband three times as bright as the practice sound's - the network trained
    # on the one could not read the other (2026-10-15, slow.wav: note column 6.1 against 1.5 generated).
    floor = np.percentile(spec, 25, axis=0, keepdims=True) + 1e-9
    return np.log1p(spec / floor).astype(np.float32)


# ---- CENTRED ON THE STATION BEING READ (2026-10-19) --------------------------------------------------------------
#
# On the IC-7610 bench Net missed MERCI, POUR CE, AU PLAISIR and both calls of bad.wav for one reason: three or four
# stations inside the receiver's filter at once (675/750/812 Hz; 688/638/562/512 Hz). Plain locks onto one note;
# Net heard them all and wrote a mixture. Told to listen only within 60 Hz of Plain's note it read 4 of the 5.
# So the network is now told WHICH station: every frame's 65 notes are taken around a centre - the note being
# followed at that moment - instead of always 300-1100 Hz. The station read is always in the middle (column
# CENTRE_COL); everything else in the window is still there, at the sides, for the network to learn to ignore.
# Same shape as before (65 notes), so the network itself is unchanged.
#
# The per-note noise floor is taken over the FULL band (0-1600 Hz, 129 notes) before the centring, so each note
# is still divided by its own floor. Notes beyond 0 or 1600 Hz are mirrored back in.
FULL_BINS = FFT // 2 + 1            # 129: 0 .. 1600 Hz
CENTRE_COL = NBINS // 2             # 32: the followed note's column
HZ_PER_BIN = RATE / FFT             # 12.5


def centre_bins(track_hz, frames):
    """The followed note per frame (a number, or an array of Hz per frame, 0 = unknown -> 700) as FFT bins."""
    t = np.broadcast_to(np.asarray(track_hz, dtype=np.float64), (frames,)).copy()
    t[~(t > 0)] = 700.0
    return np.clip(np.rint(t / HZ_PER_BIN), 0, FULL_BINS - 1).astype(np.int64)


def centred_columns(centres):
    """[T] centre bins -> [T, NBINS] full-band bin indices, mirrored at 0 and FULL_BINS-1."""
    k = centres[:, None] + (np.arange(NBINS) - CENTRE_COL)[None, :]
    k = np.abs(k)
    k = np.where(k > FULL_BINS - 1, 2 * (FULL_BINS - 1) - k, k)
    return k


def spectrogram_centred(a, track_hz):
    """float32 audio -> [T, NBINS] features centred, frame by frame, on track_hz (see above)."""
    pad = FFT // 2
    x = np.pad(a, (pad, pad), mode='reflect')
    frames = 1 + (len(x) - FFT) // HOP
    idx = np.arange(FFT)[None, :] + HOP * np.arange(frames)[:, None]
    spec = np.abs(np.fft.rfft(x[idx] * WINDOW, n=FFT, axis=1))                    # [T, 129]
    floor = np.percentile(spec, 25, axis=0, keepdims=True) + 1e-9
    feat = np.log1p(spec / floor)
    cols = centred_columns(centre_bins(track_hz, frames))
    return np.take_along_axis(feat, cols, axis=1).astype(np.float32)


def spectrogram_centred_torch(audio, centres):
    """The same for a batch on the GPU: audio [B, N], centres [B, T] (FFT bins, int64) -> [B, T, NBINS]."""
    import torch
    win = torch.hann_window(FFT, periodic=True, device=audio.device, dtype=audio.dtype)
    st = torch.stft(audio, n_fft=FFT, hop_length=HOP, win_length=FFT, window=win, center=True, pad_mode='reflect', return_complex=True)
    spec = st.abs().transpose(1, 2).float()                                          # [B, T, 129]
    floor = torch.quantile(spec, 0.25, dim=1, keepdim=True) + 1e-9
    feat = torch.log1p(spec / floor)
    off = torch.arange(NBINS, device=audio.device) - CENTRE_COL
    k = (centres.to(audio.device)[:, :, None] + off[None, None, :]).abs()
    k = torch.where(k > FULL_BINS - 1, 2 * (FULL_BINS - 1) - k, k)
    return torch.gather(feat, 2, k[:, :feat.shape[1]])


def spectrogram_slice(a, half_width_hz, center_hz=None, seed=0):
    """The same features, but the network hears only the notes within half_width_hz of center_hz (2026-10-17, his
    idea: a station at 700 Hz, noise or another station at 500 Hz). Everything outside the slice is REPLACED by
    noise - white noise put through the very same STFT, scaled to each note's own noise floor - so after the
    per-note floor it looks exactly like an empty part of the band, which is what the network was trained on.
    (Merely filtering would not do: each note is divided by its own floor, which would undo any attenuation.)
    center_hz None: the strongest steady note in the window, as Plain picks its station."""
    pad = FFT // 2
    def mag(x):
        x = np.pad(x, (pad, pad), mode='reflect')
        frames = 1 + (len(x) - FFT) // HOP
        idx = np.arange(FFT)[None, :] + HOP * np.arange(frames)[:, None]
        return np.abs(np.fft.rfft(x[idx] * WINDOW, n=FFT, axis=1))[:, BIN0:BIN1]
    spec = mag(a)
    floor = np.percentile(spec, 25, axis=0, keepdims=True) + 1e-9
    if center_hz is None:
        center_hz = (BIN0 + int(np.argmax(np.percentile(spec / floor, 95, axis=0)))) * RATE / FFT
    noise = mag(np.random.default_rng(seed).standard_normal(len(a)).astype(np.float32))
    noise = noise / (np.percentile(noise, 25, axis=0, keepdims=True) + 1e-9) * floor
    hz = (BIN0 + np.arange(NBINS)) * RATE / FFT
    keep = np.abs(hz - center_hz) <= half_width_hz
    spec = np.where(keep[None, :], spec, noise)
    floor = np.percentile(spec, 25, axis=0, keepdims=True) + 1e-9
    return np.log1p(spec / floor).astype(np.float32)


def spectrogram_torch(audio):
    """The same features as spectrogram(), for a whole batch on the GPU (or CPU): audio [B, N] float -> [B, T, NBINS].
    Checked against the numpy version to 1e-4. The window, padding and frame count match numpy's."""
    import torch
    win = torch.hann_window(FFT, periodic=True, device=audio.device, dtype=audio.dtype)
    st = torch.stft(audio, n_fft=FFT, hop_length=HOP, win_length=FFT, window=win, center=True, pad_mode='reflect', return_complex=True)
    spec = st.abs()[:, BIN0:BIN1, :].transpose(1, 2)                      # [B, T, NBINS]
    floor = torch.quantile(spec.float(), 0.25, dim=1, keepdim=True) + 1e-9
    return torch.log1p(spec.float() / floor)
