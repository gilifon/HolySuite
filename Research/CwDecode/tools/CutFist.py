"""
CUT A FISTKEYER SESSION INTO A BENCH FOLDER.

The receivers were started minutes before the keying and ran on after it. For each one this finds
where the logged keying sits in its audio - by sliding the key-down/key-up pattern from edges.tsv
along the tone's level until the two match best - and cuts from two seconds before the first element
to two seconds after the last. Files whose best match is poor (the receiver did not hear him) are
left out and said so.

Written next to the cut files:
  SENT.txt          one part per paragraph, as the other 4Z5SL folders
  edges.tsv         copied from the keying run
  offsets.tsv       per file: where time zero of edges.tsv falls in that file, in seconds (edge time t
                    sits at zero_s + t * (1 + drift_ppm / 1e6)), the exact tone
                    pitch, how well it matched and how far the tone stood above the gaps - so every
                    element in every file has an exact label

  CutFist.py <session dir with keyed/ and rx*/> <out dir> <rx subdir> [min match]
"""

import csv
import datetime as dt
import glob
import os
import shutil
import sys
import wave

import numpy as np


def envelope(x, sr, tone, hop):
    n = int(0.02 * sr); h = int(hop * sr)
    c = np.exp(-2j * np.pi * tone * np.arange(n) / sr) * np.hanning(n)
    frames = np.lib.stride_tricks.sliding_window_view(x, n)[::h]
    return 20 * np.log10(np.abs(frames @ c) + 1)


def main(session, out, rxdir, least=0.3):
    least = float(least)
    keyed = os.path.join(session, "keyed")
    start = dt.datetime.strptime(open(os.path.join(keyed, "started_utc.txt")).read().strip(), "%Y-%m-%d %H:%M:%S.%f")
    rows = list(csv.DictReader(open(os.path.join(keyed, "edges.tsv")), delimiter="\t"))
    rows = [r for r in rows if r["actual_down_ms"] != "nan"]
    first = min(float(r["actual_down_ms"]) for r in rows) / 1000
    last = max(float(r["actual_up_ms"]) for r in rows) / 1000

    hop = 0.005
    T = int(last / hop) + 1
    key = np.zeros(T)
    for r in rows:
        key[int(float(r["actual_down_ms"]) / 1000 / hop):int(float(r["actual_up_ms"]) / 1000 / hop)] = 1

    os.makedirs(out, exist_ok=True)
    lines = ["file\tzero_s\ttone_hz\tmatch\ton_off_db\tdrift_ppm"]
    for p in sorted(glob.glob(os.path.join(session, rxdir, "*.wav"))):
        name = os.path.basename(p)
        stamp = name.rsplit("_", 1)[1][:6]
        w = wave.open(p); sr = w.getframerate(); x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float); w.close()
        fstart = dt.datetime.combine(start.date(), dt.datetime.strptime(stamp, "%H%M%S").time())
        guess = (start - fstart).total_seconds()                 # the file's name says when it was asked for
        a = int(max(0, guess - 20) * sr)
        seg = x[a:a + int((last + 40) * sr)]
        if len(seg) < int((last + 1) * sr):
            print("%-40s does not cover the keying" % name); continue

        # the tone: the pitch whose level swings most - keying goes on and off, a carrier or noise does not
        n = 1024
        S = np.abs(np.fft.rfft(np.lib.stride_tricks.sliding_window_view(seg, n)[::n // 2] * np.hanning(n), axis=1))
        f = np.fft.rfftfreq(n, 1 / sr); m = (f > 200) & (f < 900)
        B = 20 * np.log10(S[:, m] + 1)
        tone = f[m][(np.percentile(B, 90, 0) - np.percentile(B, 10, 0)).argmax()]

        env = envelope(seg, sr, tone, hop)
        best = (-1.0, 0)
        for lag in range(0, len(env) - T):
            r = np.corrcoef(key, env[lag:lag + T])[0, 1]
            if r > best[0]: best = (r, lag)
        e = env[best[1]:best[1] + T]; k = key.astype(bool)
        onoff = np.median(e[k]) - np.median(e[~k])
        zero = a / sr + best[1] * hop                              # edges' time zero, in the file
        if best[0] < least:
            print("%-40s match %.2f - not heard, left out" % (name, best[0])); continue

        # TO THE MILLISECOND, AND THE CLOCKS DO NOT AGREE. The PC's clock and the receiver's sample
        # clock differ by some tens of parts per million - 14 ms by the end of a nine-minute run, a
        # quarter of a dit. So the exact pitch is found first (the one at which the known elements add
        # up loudest), then the best offset in each part, and a straight line through those offsets
        # gives time zero and the drift: edge time t sits at zero + t * (1 + drift_ppm / 1e6) in the file.
        downs = np.array([float(r["actual_down_ms"]) / 1000 for r in rows]); ups = np.array([float(r["actual_up_ms"]) / 1000 for r in rows])
        part = np.array([int(r["part"]) for r in rows])
        fine = 0.001; fh = int(fine * sr); nn = (len(x) // fh) * fh

        def coherent(pitch):
            zz = (x[:nn] * np.exp(-2j * np.pi * pitch * np.arange(nn) / sr)).reshape(-1, fh).sum(1)
            return np.concatenate([[0j], np.cumsum(zz)])

        def power(Cz, lag, m):
            ia = ((zero + lag + downs[m]) / fine).astype(int); ib = ((zero + lag + ups[m]) / fine).astype(int)
            return np.mean(np.abs(Cz[ib] - Cz[ia]) ** 2 / (ib - ia))
        allm = np.ones(len(rows), bool)
        grid = np.arange(tone - 6, tone + 6.01, 0.25)
        pitch = grid[np.argmax([power(coherent(p), 0.0, allm) for p in grid])]
        Cz = coherent(pitch)
        mids, lags = [], []
        for p in sorted(set(part)):
            m = part == p
            lg = np.arange(-0.03, 0.0301, 0.001)
            lags.append(lg[np.argmax([power(Cz, l, m) for l in lg])]); mids.append(downs[m].mean())
        slope, lag0 = np.polyfit(mids, lags, 1)
        zero += lag0

        # cut: two seconds either side of the keying
        c0 = max(0.0, zero + first - 2); c1 = zero + last * (1 + slope) + 2
        who = name.split("_")[0]
        dst = os.path.join(out, who + ".wav")
        o = wave.open(dst, "wb"); o.setnchannels(1); o.setsampwidth(2); o.setframerate(sr)
        o.writeframes(x[int(c0 * sr):int(c1 * sr)].astype(np.int16).tobytes()); o.close()
        lines.append("%s\t%.4f\t%.2f\t%.2f\t%.1f\t%.1f" % (who + ".wav", zero - c0, pitch, best[0], onoff, slope * 1e6))
        print("%-40s match %.2f  pitch %.2f Hz  %4.1f dB above the gaps  drift %+.0f ppm -> %s" % (name, best[0], pitch, onoff, slope * 1e6, dst))

    open(os.path.join(out, "offsets.tsv"), "w").write("\n".join(lines) + "\n")
    shutil.copy(os.path.join(keyed, "edges.tsv"), os.path.join(out, "edges.tsv"))
    sent = open(os.path.join(keyed, "SENT.txt")).read().split("\n")
    open(os.path.join(out, "SENT.txt"), "w").write("\n\n".join(s for s in sent if s.strip()) + "\n")


if __name__ == "__main__":
    main(*sys.argv[1:])
