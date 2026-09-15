"""
MorseAngel's OWN preparation of the audio, run here as the reference to check my C# against.

Every line follows f4exb/morseangel: nb_samples_per_dit_decim and fft_optim for the sizing,
scipy.signal.spectrogram for the transform, the three bins either side of the note summed,
and the running normalisation with its 1.5.

Prints the envelope so it can be compared, number for number, with what CwNeuralFrontEnd
produces from the same file.
"""

import sys
import wave

import numpy as np
from scipy.signal import spectrogram


def read_wav(path):
    with wave.open(path, "rb") as w:
        rate = w.getframerate()
        n = w.getnframes()
        raw = w.readframes(n)
    data = np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0
    return data, rate


def nb_samples_per_dit_decim(Fs=8000, code_speed=13, decim=7.69):
    t_dit = 1.2 / code_speed
    return int(t_dit * Fs), int(t_dit * Fs) / decim


def fft_optim(Fs=8000, code_speed=13, decim=7.69):
    spd, fft_decim = nb_samples_per_dit_decim(Fs, code_speed, decim)
    log2_spd = np.log(spd) / np.log(2)
    nfft = 2 ** int(log2_spd - 1)
    noverlap = nfft - round(fft_decim)
    return nfft, noverlap


def specimg(Fs, signal, tone, nfft, noverlap, wbins=1):
    nperseg = nfft if nfft < 256 or noverlap >= 256 else 256
    f, t, Sxx = spectrogram(signal, Fs, nfft=nfft, noverlap=noverlap,
                            nperseg=nperseg, scaling="density")
    fbin = (tone / (Fs / 2)) * (len(f) - 1)
    center = int(round(fbin))
    lo = max(0, center - wbins)
    hi = min(len(f), center + wbins + 1)
    return f[lo:hi], t, Sxx[lo:hi, :]


def main(path, tone, wpm):
    signal, rate = read_wav(path)
    nfft, noverlap = fft_optim(rate, wpm)

    f, t, img = specimg(rate, signal, tone, nfft, noverlap, 1)
    line = np.sum(img, axis=0)

    print("# rate=%d wpm=%s tone=%s nfft=%d noverlap=%d hop=%d"
          % (rate, wpm, tone, nfft, noverlap,
             (nfft if nfft < 256 or noverlap >= 256 else 256) - noverlap))
    print("# envelope numbers: %d" % len(line))

    # MorseAngel's normalisation: the scale is max/1.5 and everything above 1 is clipped.
    norm = max(line) / 1.5
    out = line / norm
    out[out > 1] = 1

    for v in out:
        print("%.5f" % v)


if __name__ == "__main__":
    main(sys.argv[1], float(sys.argv[2]), float(sys.argv[3]))
