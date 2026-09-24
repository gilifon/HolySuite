"""
REAL BAND WITH NOTHING TO READ, AS "SAY NOTHING HERE" LESSONS FOR THE LETTER NETWORK.

The network's worst fault on his transmissions is inventing letters: it reads more words than the
plain decoder and prints far more that were never sent (969 against 704), most of them in the noise
between his parts. The only silence it had been taught was generated noise and two recordings.

The four evenings recorded in Europe at 17:00 US Eastern hold no W1AW (measured) - hours of the real
40-metre band at night: noise, static, RTTY and other digital signals, now and then somebody's CW.
Cut into twelve-second pieces with NO labels, they teach "nothing to say here" on real audio.

BUT NOT WHERE SOMEBODY IS SENDING CW. A piece labelled "nothing" over a real CW station would teach
the network to ignore real CW. So every piece is checked against the plain decoder's own belief
(GateMask.ps1: 2 = keying proved to be Morse) and dropped if Morse was proved in more than a small
share of it.

  MakeQuiet.py gatefile.txt wavfolder outfolder prefix [most]
"""

import os
import sys
import wave

import numpy as np

PIECE_SECONDS = 12
MOST_MORSE = 0.02          # share of a piece the plain decoder may call proved Morse


def main(gatefile, folder, outdir, prefix, most=400):
    most = int(most)
    os.makedirs(outdir, exist_ok=True)
    index = open(os.path.join(outdir, 'index.txt'), 'a', encoding='utf-8')
    written = dropped = 0
    rng = np.random.RandomState(7)

    gates = {}
    for line in open(gatefile, encoding='utf-8'):
        name, mask = line.rstrip('\n').split('\t', 1)
        gates[name] = mask

    names = sorted(gates)
    rng.shuffle(names)
    per_file = max(1, most // max(1, len(names)) + 1)

    for name in names:
        path = os.path.join(folder, name + '.wav')
        if not os.path.exists(path):
            continue
        w = wave.open(path)
        rate = w.getframerate()
        audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
        w.close()
        mask = gates[name]
        step = PIECE_SECONDS * rate
        starts = list(range(0, len(audio) - step, step))
        rng.shuffle(starts)
        kept = 0
        for a in starts:
            if kept >= per_file or written >= most:
                break
            m0, m1 = a // 40, (a + step) // 40            # a reading every 40 samples (5 ms at 8 kHz)
            span = mask[m0:m1]
            if not span:
                continue
            if span.count('2') / float(len(span)) > MOST_MORSE:
                dropped += 1
                continue
            piece = '%s_%s_%d' % (prefix, name, a // rate)
            o = wave.open(os.path.join(outdir, piece + '.wav'), 'wb')
            o.setnchannels(1); o.setsampwidth(2); o.setframerate(rate)
            o.writeframes(audio[a:a + step].tobytes())
            o.close()
            index.write('%s\t\t\t\t\t\t\n' % piece)
            kept += 1
            written += 1
    index.close()
    print('%d quiet pieces written, %d left out because somebody was sending Morse' % (written, dropped))


if __name__ == '__main__':
    main(*sys.argv[1:])
