# The blind whole-element decoder over every recording in a folder, for ScoreText.py.
# No key log: the pitch starts from the strongest note in 300-900 Hz, as the plain decoder's bank would.
import sys, os, glob, wave
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ElementBlind as B, ElementCoherent as E
folder, out = sys.argv[1], sys.argv[2]
only = sys.argv[3] if len(sys.argv) > 3 else None
with open(out, 'w', encoding='utf-8') as f:
    for p in sorted(glob.glob(os.path.join(folder, '*.wav'))):
        name = os.path.basename(p)[:-4]
        if only and only not in name: continue
        w = wave.open(p); sr = w.getframerate(); x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float); w.close()
        n = 4096; S = np.abs(np.fft.rfft(np.lib.stride_tricks.sliding_window_view(x, n)[::n] * np.hanning(n), axis=1)).mean(0)
        fr = np.fft.rfftfreq(n, 1 / sr); m = (fr > 300) & (fr < 900)
        rough = round(fr[m][S[m].argmax()] / 25) * 25
        pitch = B.find_pitch(x, sr, rough)
        z = E.mixed_frames(x, sr, pitch); amp, noise = B.levels(z); unit = B.speed(z, amp, noise)
        text, _ = E.decode(z, unit, amp, noise)
        f.write('%s\t%s\n' % (name, text)); f.flush()
        print(name, pitch, text[:120]); sys.stdout.flush()
