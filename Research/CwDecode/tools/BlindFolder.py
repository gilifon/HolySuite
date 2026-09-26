# The blind whole-element decoder over every recording in a folder, for ScoreText.py.
# No key log: the pitch starts from the strongest note in 300-900 Hz, as the plain decoder's bank would.
import sys, os, glob, wave
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ElementBlind as B, ElementCoherent as E
STRONG_DB = float(os.environ.get('STRONG_DB', '0'))
STRONG_CHUNK = int(os.environ.get('STRONG_CHUNK', '2'))
TRACK = os.environ.get('TRACK_PITCH', '1') == '1'
LEARN = os.environ.get('LEARN_TIMING', '1') == '1'
ADAPT = os.environ.get('ADAPT_CHUNK', '1') == '1'
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
        if TRACK:
            z, ps = B.tracked_frames(x, sr); pitch = ps[0]
        else:
            pitch = B.find_pitch(x, sr, rough); z = E.mixed_frames(x, sr, pitch)
        amp, noise = B.levels(z); unit = B.speed(z, amp, noise)
        # pieces about a third of a dit: 20 ms pieces blur a 30 ms dit at 40 WPM
        # PIECES OF 4 (20 ms), SHORTER ONLY FOR FAST SENDING. A third of a dit everywhere was measured:
        # longer pieces for slow sending cost the air sessions (751 read / 659 invented fell to 715 /
        # 719), and so did pieces of 3 for middle speeds; only below a 45 ms dit do shorter pieces pay
        # (the fast French QSO went from 5 of 9 words to 8).
        u = np.median(unit)
        if ADAPT and u < 9: E.CHUNK = int(np.clip(round(u / 3), 2, 4))
        # A STRONG SIGNAL NEEDS NO PIECES: a piece of c frames smears each tone c-1 frames into the gaps
        # around it, which on a weak signal buys noise protection and on a strong one only blurs the gaps.
        snr = 10 * np.log10(np.median(amp ** 2) / np.median(noise))
        if STRONG_DB and snr > STRONG_DB: E.CHUNK = min(E.CHUNK, STRONG_CHUNK)
        _, segs = E.decode(z, unit, amp, noise)
        if LEARN:
            # THE OPERATOR'S OWN TIMING. The first reading assumes the book - dah 3, gaps 1, 3, 7 -
            # and real fists do not keep to it (a bug's long dahs, a hand's stretched letter gaps).
            # Each kind's length is taken from that reading, in dits, and the audio read again with it.
            lens = {k: [] for k in ('dit', 'dah', 'egap', 'lgap', 'wgap')}
            for kind, a, b in segs:
                lens[kind].append((b - a) / unit[a])
            dit = np.median(lens['dit']) if len(lens['dit']) > 10 else 1.0
            lim = {'dah': (2.4, 4.5), 'egap': (0.6, 1.6), 'lgap': (2.2, 5.0), 'wgap': (5.0, 12.0)}
            book = dict((n, u) for _, u, n in E.KINDS)
            learned = {'dit': 1.0}
            for kind, (lo, hi) in lim.items():
                v = lens[kind]
                learned[kind] = float(np.clip(np.median(v) / dit, lo, hi)) if len(v) > 10 else book[kind]
            keep = E.KINDS
            E.KINDS = [(m, learned[n], n) for m, _, n in keep]
            _, segs = E.decode(z, unit * dit, amp, noise)
            E.KINDS = keep
            print('learned', ' '.join('%s %.2f' % (n, learned[n]) for n in ('dah', 'egap', 'lgap', 'wgap')), 'dit x%.2f' % dit)
        E.CHUNK = 4
        text = B.gated_text(segs, E.decode.F)
        f.write('%s\t%s\n' % (name, text)); f.flush()
        print(name, pitch, text[:120]); sys.stdout.flush()
