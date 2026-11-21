"""
THE PRACTICE SOUND (2026-10-14): generated CW with what real bands do to it, laid over REAL noise.

One sample = a short stretch of band: noise cut from our own recordings (noise_bank.npy), and on it one or
more OVERS, each sent by a station of its own - note, speed, hand (jitter, dah length, gap stretching),
strength, fading, receiver tail, key clicks. Between overs there are pauses; the next over is often another
station on another note (a QSO). Some samples add interference: another station, a steady carrier, static
crashes. Some are only noise (the right answer is then nothing). The label is the text of the overs, in order.

What September's practice sound lacked and this has: real noise instead of white hiss; fading;
AGC pumping; many hands; a new station beginning on a new note; and a wide range of strengths down to
barely above the noise (a station 0 dB over the noise in its own 50 Hz).

The texts are made of: real callsigns (HolyLogger's callsign list), CW chatter and Q-codes, report and serial
numbers, names, English words (Python's own documentation as a corpus), and code groups. NOTHING from the
W1AW bulletin or from the texts we test on is used.
"""
import os
import re
import sys
import numpy as np
from scipy.signal import lfilter, welch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import *

CALLS = None
ENGLISH = None
NOISE = None

PHRASES = ["CQ", "DE", "K", "KN", "R", "TNX", "FB", "UR", "RST", "QTH", "NAME", "OP", "WX", "TEMP", "RIG", "ANT", "PWR", "73", "GL",
           "HPE", "CUL", "AGN", "PSE", "BK", "SK", "TU", "ES", "HR", "VY", "WID", "GM", "GA", "GE", "OM", "YL", "XYL", "HW", "CPY",
           "QRZ", "QRL", "QRM", "QRN", "QSB", "QSY", "QRP", "QRO", "QSL", "FER", "NR", "AGN", "SRI", "CFM", "BTU", "DX", "TEST",
           "5NN", "599", "579", "589", "559", "449", "UP", "DWN", "CQ DX", "CQ TEST", "POTA", "SOTA", "TKS", "FNQSO", "SIG", "STRONG"]
NAMES = ["BOB", "JOHN", "PETER", "LARS", "SERGEJ", "TOM", "JIM", "MIKE", "ENRICO", "HANS", "PAUL", "DAVID", "ANDY", "ZOLI", "JAN", "PIERRE", "ALEX", "MARK", "STEVE"]
# WORDS OF OTHER LANGUAGES (2026-10-19). Net missed a French QSO on the IC-7610 bench: all its practice words
# were English, so it leant towards English spellings. These are everyday words of QSOs in other languages - and
# NOT the bench's own words (radio.wav: MERCI, POUR, CE, AU, PLAISIR, SOIR, BONNE, SUR, DELTA), so that bench
# stays a fair test of reading words it has never been taught.
FOREIGN = ["BONJOUR", "BONSOIR", "SALUT", "AMI", "AMITIES", "CORDIALES", "CHER", "TEMPS", "BEAU", "PLUIE", "ANTENNE",
           "PUISSANCE", "REPORT", "NOM", "VILLE", "CHEZ", "MOI", "VOUS", "TRES", "BIEN", "RECU", "ENCORE", "PROCHAINE",
           "FOIS", "ADIEU", "MATIN", "JOUR", "NUIT", "CONTACT", "BELLE", "FRANCE", "PARIS", "LYON",
           "DANKE", "GUTEN", "TAG", "ABEND", "MORGEN", "VIELEN", "DANK", "GRUSS", "WETTER", "SONNE", "REGEN", "WIND",
           "ANTENNE", "LEISTUNG", "NAME", "HIER", "SCHOEN", "WIEDER", "BIS", "BALD", "TSCHUESS", "FREUND", "BERLIN",
           "GRAZIE", "MILLE", "BUONA", "SERA", "BUONGIORNO", "CIAO", "AMICO", "TEMPO", "SOLE", "PIOGGIA", "NOME",
           "CITTA", "SALUTI", "ARRIVEDERCI", "PROSSIMA", "VOLTA", "ROMA", "MILANO",
           "HOLA", "GRACIAS", "BUENAS", "TARDES", "NOCHES", "AMIGO", "TIEMPO", "NOMBRE", "SALUDOS", "HASTA", "LUEGO",
           "PRONTO", "MADRID", "OBRIGADO", "BOA", "NOITE", "TARDE", "ABRACO", "LISBOA",
           "DANK", "GOEDE", "AVOND", "MIDDAG", "GROETEN", "TOT", "ZIENS", "DZIEKUJE", "DOBRY", "WIECZOR", "POZDRAWIAM",
           "SPASIBO", "PRIVET", "DOBRE", "TAK", "TACK", "HEJ", "KIITOS", "HYVAA"]

# Contest exchange numbers: 1 to 4 figures, sometimes with leading zeros - but never the bench's own 7388.
def serial(rng):
    n = int(rng.integers(1, 10000)) if rng.random() < 0.6 else int(rng.integers(1, 1000))
    if n == 7388:
        n = 7389
    return ("%04d" % n) if (n < 1000 and rng.random() < 0.3) else str(n)


TOWNS = ["NY", "ROMA", "WIEN", "PARIS", "BERLIN", "TEL AVIV", "KRAKOW", "BUDAPEST", "PRAHA", "HAIFA", "OHIO", "TEXAS", "LONDON", "OSLO"]


def load():
    """Data files: next to this file when it is a training package (callsigns.txt, english.txt), else from the
    HolyLogger source tree and Python's own documentation."""
    global CALLS, ENGLISH, NOISE
    if CALLS is not None:
        return
    local = os.path.join(HERE, 'callsigns.txt')
    path = local if os.path.exists(local) else os.path.join(HERE, '..', '..', '..', 'HolyLogger', 'Data', 'callsigns_merged_big.txt')
    lines = open(path, encoding='utf-8', errors='ignore').read().split()
    CALLS = [l for l in lines if 4 <= len(l) <= 7 and re.fullmatch(r'[A-Z0-9]+', l) and re.search(r'\d', l) and re.search(r'[A-Z]$', l)]
    eng = os.path.join(HERE, 'english.txt')
    if os.path.exists(eng):
        ENGLISH = open(eng, encoding='utf-8').read().split()
    else:
        topics = os.path.join(sys.base_prefix, 'Lib', 'pydoc_data', 'topics.py')
        text = open(topics, encoding='utf-8', errors='ignore').read().upper()
        ENGLISH = [w for w in re.findall(r"[A-Z]{2,11}", text)]
    NOISE = np.load(os.path.join(HERE, 'noise_bank.npy')).astype(np.float32)
    global NOISE_BY_KIND
    kinds = os.path.join(HERE, 'noise_kind.npy')
    if os.path.exists(kinds):
        k = np.load(kinds)
        NOISE_BY_KIND = [np.flatnonzero(k == 0), np.flatnonzero(k == 1)]


# His own radio's share of the noise (second run, 2026-10-17): the Europe nights added many more KiwiSDR
# blocks than there are of his IC-7610, and picked evenly they would have pushed his radio's sound out.
NOISE_BY_KIND = None
OWN_RADIO_SHARE = 0.4


def make_call(rng):
    if rng.random() < 0.7:
        c = CALLS[rng.integers(len(CALLS))]
    else:
        pre = rng.choice(["W", "K", "N", "AA", "DL", "DJ", "OK", "SP", "HA", "I", "IK", "F", "G", "M", "EA", "OH", "SM", "LY", "LZ", "UA", "RA", "R", "YO", "4Z", "4X", "9A", "OE", "ON", "PA", "S5", "VK", "JA", "UT", "US"])
        c = pre + str(rng.integers(10)) + ''.join(rng.choice(list("ABCDEFGHIJKLMNOPQRSTUVWXYZ"), rng.integers(1, 4)))
    if rng.random() < 0.06:
        c += rng.choice(["/P", "/M", "/QRP", "/A"])
    return c


def make_word(rng):
    r = rng.random()
    if r < 0.22:
        return ENGLISH[rng.integers(len(ENGLISH))]
    if r < 0.30:
        return FOREIGN[rng.integers(len(FOREIGN))]
    if r < 0.52:
        return PHRASES[rng.integers(len(PHRASES))]
    if r < 0.68:
        return make_call(rng)
    if r < 0.78:
        return str(rng.integers(1, 1000)) if rng.random() < 0.6 else str(rng.integers(100, 600))
    if r < 0.85:
        return NAMES[rng.integers(len(NAMES))]
    if r < 0.89:
        return TOWNS[rng.integers(len(TOWNS))]
    if r < 0.94:
        return ''.join(rng.choice(list("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"), rng.integers(3, 6)))
    return rng.choice(["=", "+", "?", ".", ",", "/"]) if rng.random() < 0.5 else PHRASES[rng.integers(len(PHRASES))]


def make_over_text(rng, max_seconds, unit):
    """Text for one over, as long as a random share of the time there is (a short call or a long ramble)."""
    kind = rng.random()
    me, you = make_call(rng), make_call(rng)
    if kind < 0.20:
        words = ["CQ", "CQ", "DE", me, me, "K"] if rng.random() < 0.5 else ["CQ", "TEST", "DE", me, me]
        fill = False
    elif kind < 0.30:
        # a contest exchange (2026-10-19: Net missed "7388" on the IC-7610 bench)
        rst = rng.choice(["5NN", "599", "5NN", "ENN"])
        forms = [[you, rst, serial(rng)], ["TU", rst, serial(rng)], [rst, serial(rng), "TU"],
                 ["R", rst, serial(rng), you], [you, rst, serial(rng), serial(rng)]]
        words = forms[int(rng.integers(len(forms)))]
        fill = False
    elif kind < 0.55:
        words = [you, "DE", me]
        fill = True
    elif kind < 0.85:
        words = []
        fill = True
    else:
        words = [''.join(rng.choice(list("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"), 5))]
        fill = True
    target = rng.uniform(0.3, 1.0) * max_seconds
    def length(ws):
        return sum(sum(sum(3 if s == '-' else 1 for s in MORSE[c]) + len(MORSE[c]) - 1 + 3 for c in w if c in MORSE) + 4 for w in ws) * unit
    while fill and length(words) < target and len(words) < 40:
        if kind >= 0.85:
            words.append(''.join(rng.choice(list("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"), 5)))
        else:
            words.append(make_word(rng))
    while words and length(words) > max_seconds:
        words.pop()
    if words and fill and rng.random() < 0.4 and len(words) > 2:
        words.append("K")
    return ' '.join(words)


def keying(rng, text, wpm):
    """-> (key-down intervals, end time, letter events) for the text, with a hand."""
    unit = 1.2 / wpm
    jit = rng.uniform(0.02, 0.14)               # how unevenly his hand sends
    dah = rng.uniform(2.7, 3.4)
    egap = rng.uniform(0.8, 1.2)
    lgap = rng.uniform(2.4, 4.6)
    wgap = rng.uniform(5.5, 10.0)
    drift = rng.uniform(0.0, 0.05)
    t = 0.0
    marks = []
    events = []                                  # (seconds, character) - where each letter is "said", in the gap after it
    u = unit
    prev_end = None
    for wi, word in enumerate(text.split(' ')):
        if wi > 0:
            t += (wgap - lgap) * u * (1 + rng.normal(0, jit))     # the letter gap before it is added below
        first_of_word = True
        for ci, c in enumerate(word):
            if c not in MORSE:
                continue
            if ci > 0 or wi > 0:
                t += lgap * u * (1 + rng.normal(0, jit * 1.3))
            if first_of_word and wi > 0 and prev_end is not None:
                events.append((prev_end + 0.5 * (t - prev_end), ' '))
            first_of_word = False
            for ei, e in enumerate(MORSE[c]):
                if ei > 0:
                    t += egap * u * max(0.3, 1 + rng.normal(0, jit))
                ln = (dah if e == '-' else 1.0) * u * max(0.4, 1 + rng.normal(0, jit))
                marks.append((t, t + ln))
                t += ln
            prev_end = t
            events.append((t + 0.8 * u, c))
            u *= 1 + rng.normal(0, drift) * 0.15
            u = min(max(u, unit * 0.8), unit * 1.25)
    return marks, t, events


def smooth_noise(rng, n, tau_s, fs=RATE):
    a = np.exp(-1.0 / (tau_s * fs))
    x = lfilter([1 - a], [1, -a], rng.standard_normal(n + 2000))[2000:]
    return x / (x.std() + 1e-9)


def render_station(rng, marks, n, f0, wpm, depth, tail, click):
    """Complex-free tone with keying envelope, receiver tail, fading, drift and key clicks (unit amplitude)."""
    raw = np.zeros(n, dtype=np.float32)
    for a, b in marks:
        i, j = int(a * RATE), int(b * RATE)
        if i >= n:
            break
        raw[i:min(j, n)] = 1.0
    a_rise = np.exp(-1.0 / (rng.uniform(0.001, 0.004) * RATE))
    env = lfilter([1 - a_rise], [1, -a_rise], raw)
    a_tail = np.exp(-1.0 / (tail * RATE))
    env2 = lfilter([1 - a_tail], [1, -a_tail], env)
    env = np.maximum(env2, 0)
    drift_hz = smooth_noise(rng, n, 2.0) * rng.uniform(0.0, 1.5)         # the note wanders by about a Hz or so
    phase = 2 * np.pi * np.cumsum(f0 + drift_hz) / RATE + rng.uniform(0, 6.28)
    tone = np.cos(phase) * env
    if depth > 0:
        fade = np.exp(depth * smooth_noise(rng, n, rng.uniform(0.3, 4.0)))
        if rng.random() < 0.3:                                    # a deep selective fade
            c = rng.integers(0, n); w = int(RATE * rng.uniform(0.4, 2.0))
            g = 1 - rng.uniform(0.6, 0.95) * np.exp(-0.5 * ((np.arange(n) - c) / w) ** 2)
            fade = fade * g
        tone = tone * fade / (fade.mean() + 1e-9)
    if click > 0:
        d = np.diff(raw, prepend=0.0)
        for i in np.nonzero(d)[0]:
            L = int(RATE * 0.004)
            if i + L < n:
                tone[i:i + L] += click * np.sign(d[i]) * rng.standard_normal(L) * np.exp(-np.arange(L) / (0.0012 * RATE))
    return tone.astype(np.float32)


def noise_for(rng, n):
    out = []
    got = 0
    pool = None
    if NOISE_BY_KIND is not None and len(NOISE_BY_KIND[0]) and len(NOISE_BY_KIND[1]):
        pool = NOISE_BY_KIND[0] if rng.random() < OWN_RADIO_SHARE else NOISE_BY_KIND[1]
    while got < n:
        b = NOISE[pool[rng.integers(len(pool))]] if pool is not None else NOISE[rng.integers(len(NOISE))]
        if rng.random() < 0.5:
            b = b[::-1]
        out.append(b * rng.choice([-1.0, 1.0]))
        got += len(b)
    x = np.concatenate(out)[:n].astype(np.float32)
    # a gentle tilt, so the same recording does not always come out with the same hump
    if rng.random() < 0.6:
        tilt = rng.uniform(-0.6, 0.6)
        X = np.fft.rfft(x)
        f = np.fft.rfftfreq(len(x), 1 / RATE)
        x = np.fft.irfft(X * (1 + tilt * (f - 700) / 700), len(x)).astype(np.float32)
    return x / (x.std() + 1e-9)


class Psd:
    """The noise's power spectrum, worked out ONCE per sample (it was 60% of the generator's time, once per station)."""
    def __init__(self, noise):
        self.f, self.p = welch(noise, fs=RATE, nperseg=512)

    def at(self, f0):
        return float(np.mean(self.p[(self.f > f0 - 40) & (self.f < f0 + 40)])) + 1e-12


WEAK = float(os.environ['GEN_WEAK']) if os.environ.get('GEN_WEAK') else None


def make_sample(rng, seconds=15.0, silent_prob=0.12, ease=0.0, with_track=False):
    """ease: dB added to every station's strength and a cut in interference - 0 is the real thing, more is gentler (the first steps of training).

    with_track (2026-10-19, the centred network - see common.spectrogram_centred): also returns the note Plain would be
    following, frame by frame (Hz), and the sample is a CROWDED band: other stations near the followed one, at the
    same time, sometimes stronger - and only the followed station's text is in the label. The track moves to each new
    over's note a little after the over begins (Plain needs a moment to lock on), and is a few Hz off, as Plain's is."""
    load()
    n = int(seconds * RATE)
    frames = 1 + n // HOP
    track = np.zeros(frames, dtype=np.float32)
    locks = []                                  # (seconds, note): the track moves to `note` at `seconds`
    noise = noise_for(rng, n)
    psd = Psd(noise)
    audio = noise.copy()
    parts = []
    events = []
    # NOTHING SENT (second run, 2026-10-17: 12%, was 4%, and no longer bare noise). The first network made up
    # letters on an empty band - the IC-7610 bench's two quiet recordings cost it 3-4 words - and its quiet
    # practice had been clean noise only, never the crashes and carriers that a real empty band has. Now a
    # silent sample gets those too (but never another station: that would teach it to ignore real CW).
    silent = rng.random() < silent_prob
    t = rng.uniform(0.2, 2.5)
    station = None
    first = True
    while not silent and t < seconds - 2.0:
        if station is None or rng.random() < 0.45:
            # GEN_WEAK (2026-10-27): the share of stations at -3..12 dB, else 30% at -3..8. His on-air sessions measure
            # 4-12 dB in this scale (signal_level.py), W1AW 17-32 - the practice had little where his sessions are.
            snr_kind = rng.random()
            if WEAK is not None:
                snr = rng.uniform(-3, 12) if snr_kind < WEAK else rng.uniform(12, 38)
            else:
                snr = rng.uniform(-3, 8) if snr_kind < 0.30 else (rng.uniform(8, 20) if snr_kind < 0.7 else rng.uniform(20, 38))
            station = dict(f0=rng.uniform(320, 1080), wpm=float(np.clip(rng.lognormal(np.log(21), 0.35), 7, 46)),
                           snr=snr + ease, depth=rng.choice([0.0, 0.0, 0.15, 0.4, 0.8, 1.2]), tail=rng.uniform(0.002, 0.035),
                           click=rng.choice([0.0, 0.0, 0.3, 0.8]))
        unit = 1.2 / station['wpm']
        text = make_over_text(rng, seconds - t - 0.8, unit)
        if not text:
            break
        marks, dur, ev = keying(rng, text, station['wpm'])
        if t + dur > seconds - 0.3:
            # drop what does not fit
            keep = text.split(' ')
            while keep and t + keying(rng, ' '.join(keep), station['wpm'])[1] > seconds - 0.3:
                keep.pop()
            if not keep:
                break
            text = ' '.join(keep)
            marks, dur, ev = keying(rng, text, station['wpm'])
        marks = [(a + t, b + t) for a, b in marks]
        events += [(a + t, c) for a, c in ev]
        locks.append((t + rng.uniform(0.15, 1.0), station['f0'] + rng.uniform(-8, 8)))
        sig = render_station(rng, marks, n, station['f0'], station['wpm'], station['depth'], station['tail'], station['click'])
        amp = np.sqrt(2 * 10 ** (station['snr'] / 10) * psd.at(station['f0']) * 80.0)   # tone power vs noise in its own ~80 Hz
        audio = audio + amp * sig
        parts.append(text)
        t += dur + rng.uniform(0.4, 3.6)
    if with_track:
        # THE TRACK: before the first lock, either already on the first over's note or somewhere else
        if locks:
            first_note = locks[0][1] if rng.random() < 0.6 else rng.uniform(320, 1080)
        else:
            first_note = rng.uniform(320, 1080)
        cur, li = first_note, 0
        for f in range(frames):
            tf = f * HOP / RATE
            while li < len(locks) and locks[li][0] <= tf:
                cur = locks[li][1]
                li += 1
            track[f] = cur
        track = track + rng.normal(0, 2.0, frames).astype(np.float32)
        # THE CROWD: 1-3 other stations, mostly close to a followed note (25-250 Hz off), keying at the same time,
        # from much weaker to stronger than the one followed. Never within 25 Hz of the track (that would be the
        # same station). Not in the label. Also on silent samples: the network must not read a station that is
        # not in the middle.
        if rng.random() < (0.5 if silent else 0.6):
            notes = [l[1] for l in locks] or [float(track[0])]
            for _ in range(int(rng.integers(1, 4))):
                for _try in range(10):
                    base = notes[int(rng.integers(len(notes)))]
                    f1 = base + rng.choice([-1, 1]) * rng.uniform(25, 250) if rng.random() < 0.7 else rng.uniform(320, 1080)
                    if 300 < f1 < 1100 and np.min(np.abs(track - f1)) > 25:
                        break
                else:
                    continue
                w1 = rng.uniform(12, 35)
                txt = make_over_text(rng, seconds, 1.2 / w1)
                if not txt:
                    continue
                marks, _, _ = keying(rng, txt, w1)
                t1 = rng.uniform(-3, seconds - 2)
                marks = [(a + t1, b + t1) for a, b in marks if 0 <= a + t1 and b + t1 < seconds]
                if not marks:
                    continue
                sig = render_station(rng, marks, n, f1, w1, rng.choice([0.0, 0.15, 0.4, 0.8]), rng.uniform(0.002, 0.03), rng.choice([0.0, 0.3]))
                snr = rng.uniform(-6, 30)
                audio = audio + np.sqrt(2 * 10 ** (snr / 10) * psd.at(f1) * 80.0) * sig
    # interference that is not in the label
    if not with_track and not silent and rng.random() < 0.25 * (1 - min(1.0, ease / 12.0)):
        f1 = rng.uniform(320, 1080)
        txt = make_over_text(rng, seconds, 1.2 / rng.uniform(12, 32))
        if txt:
            marks, _, _ = keying(rng, txt, rng.uniform(12, 32))
            marks = [(a + rng.uniform(0, 3), b) for a, b in marks]
            marks = [(a, b + (a - b) * 0 + 0.0) for a, b in marks]
            sig = render_station(rng, marks, n, f1, 20, 0.3, 0.01, 0.0)
            snr = rng.uniform(-6, 14)
            audio = audio + np.sqrt(2 * 10 ** (snr / 10) * psd.at(f1) * 80.0) * sig
    if rng.random() < (0.2 if silent else 0.06):
        f1 = rng.uniform(320, 1080)
        audio = audio + np.sqrt(2 * 10 ** (rng.uniform(-4, 14) / 10) * psd.at(f1) * 80.0) * np.cos(2 * np.pi * f1 * np.arange(n) / RATE)
    if rng.random() < (0.6 if silent else 0.35):                       # static crashes
        for _ in range(rng.poisson(rng.uniform(0.3, 4.0))):
            c = rng.integers(0, n - 200); L = int(RATE * rng.uniform(0.005, 0.04))
            audio[c:c + L] += rng.standard_normal(min(L, n - c)) * rng.uniform(3, 12)
    if rng.random() < 0.7:                                             # AGC: the radio turns itself down on strong sound
        k = rng.uniform(0.3, 3.0); tau = rng.uniform(0.08, 0.8)
        a = np.exp(-1.0 / (tau * RATE))
        level = lfilter([1 - a], [1, -a], np.abs(audio))
        audio = audio / (1 + k * np.maximum(level - 1.2, 0))
    if with_track:
        return audio.astype(np.float32), ' '.join(parts), events, track
    return audio.astype(np.float32), ' '.join(parts), events


def encode(text):
    return np.array([C2I[c] for c in text if c in C2I], dtype=np.int64)


if __name__ == '__main__':
    import time
    rng = np.random.default_rng(1)
    t0 = time.time()
    for i in range(20):
        a, txt, ev = make_sample(rng)
        f = spectrogram(a)
    print("20 samples in %.1f s (%.2f s each)" % (time.time() - t0, (time.time() - t0) / 20), a.shape, f.shape)
    for i in range(6):
        a, txt, ev = make_sample(rng)
        print(repr(txt), len(ev))

