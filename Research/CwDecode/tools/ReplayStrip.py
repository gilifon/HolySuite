"""
THE REPLAY TOOL: a recording drawn as the decode window draws it, in pictures that can be stepped
through back and forth (his idea - instead of a screen video).

For every N seconds of the recording, one picture: the waterfall exactly as the window paints it
(time left to right here, so the pictures read in order; 300-1000 Hz up; each note's own quiet level;
Blackman-Harris; the same colours), a second ruler, and under it the two lines of letters - Plain in
white, New in yellow - each under the marks it was read from (see ReplayLetters.cs for where they
come from). A missed or wrong letter can then be put against its own dits and dahs.

  ReplayStrip.py <recording.wav> [seconds per picture, default 10] [out folder, default build/replay]

Builds and runs ReplayLetters.exe itself.
"""

import os
import subprocess
import sys
import wave

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
BUILD = os.path.join(HERE, '..', 'build')
LOW, HIGH, ROWS, ROW_PX, COL_PX = 300.0, 1000.0, 56, 2, 1
STOPS = np.array([[0, 0, 0], [10, 30, 110], [255, 200, 0], [255, 255, 255]], float)


def palette(v):
    t = v * (len(STOPS) - 1)
    k = np.minimum(len(STOPS) - 2, t.astype(int)); w = t - k
    return STOPS[k] + (STOPS[k + 1] - STOPS[k]) * w[..., None]


def waterfall(x, sr):
    """Columns every 10 ms, as CwDecodeWindow.DrawWaterfall: 40 ms Blackman-Harris, per-row floor."""
    win = sr * 40 // 1000; hop = sr // 100
    nfft = 1
    while nfft < win * 3 // 2: nfft <<= 1
    n = np.arange(win)
    a = 2 * np.pi * n / (win - 1)
    bh = 0.35875 - 0.48829 * np.cos(a) + 0.14128 * np.cos(2 * a) - 0.01168 * np.cos(3 * a)
    f = HIGH - (HIGH - LOW) * np.arange(ROWS) / (ROWS - 1)
    b = f / (sr / nfft); b0 = b.astype(int); w = b - b0
    cols = []; floor = None
    for i in range(0, len(x) - win, hop):
        m = np.abs(np.fft.rfft(x[i:i + win] * bh, nfft))
        db = 20 * np.log10(m[b0] + (m[b0 + 1] - m[b0]) * w + 1)
        if floor is None: floor = db.copy()
        floor += np.where(db < floor, 0.05, 0.002) * (db - floor)
        cols.append(np.clip((db - floor - 6) / 30, 0, 1))
    return np.array(cols), hop, win


def main(path, seconds='10', out=None):
    seconds = float(seconds)
    out = out or os.path.join(BUILD, 'replay')
    os.makedirs(out, exist_ok=True)
    name = os.path.splitext(os.path.basename(path))[0]

    exe = os.path.join(BUILD, 'ReplayLetters.exe')
    csc = r'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    app = os.path.join(HERE, '..', '..', '..', 'HolyLogger')
    subprocess.run([csc, '/nologo', '/o', '/out:' + exe, os.path.join(HERE, 'ReplayLetters.cs'),
                    os.path.join(app, 'CwElementDecoder.cs'), os.path.join(app, 'CwDecoder.cs')], check=True,
                   stdout=subprocess.DEVNULL)
    tsv = os.path.join(out, name + '_letters.tsv')
    texts = subprocess.run([exe, path, tsv], capture_output=True, text=True).stdout
    open(os.path.join(out, name + '_text.txt'), 'w', encoding='utf-8').write(texts)

    letters = []
    for line in open(tsv, encoding='utf-8').read().splitlines()[1:]:
        reader, letter, sample = line.split('\t')[:3]
        letters.append((reader, letter, int(sample)))

    w = wave.open(path); sr = w.getframerate()
    x = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(float); w.close()
    cols, hop, win = waterfall(x, sr)

    try:
        font = ImageFont.truetype('consolab.ttf', 16)
        small = ImageFont.truetype('consola.ttf', 12)
    except OSError:
        font = small = ImageFont.load_default()

    per = int(seconds * 100)
    wf_h = ROWS * ROW_PX; ruler_h = 16; letter_h = 22
    made = []
    for start in range(0, len(cols), per):
        part = cols[start:start + per]
        img = Image.new('RGB', (per * COL_PX, wf_h + ruler_h + 2 * letter_h), (0, 0, 0))
        pix = palette(part.T).astype(np.uint8)                    # rows x cols x 3
        pix = np.repeat(np.repeat(pix, ROW_PX, axis=0), COL_PX, axis=1)
        img.paste(Image.fromarray(pix), (0, 0))
        d = ImageDraw.Draw(img)
        t0 = start / 100.0
        for s in range(int(np.ceil(t0)), int(t0 + seconds) + 1):   # the second ruler
            xs = int((s - t0) * 100 * COL_PX)
            d.line([(xs, wf_h), (xs, wf_h + 5)], fill=(160, 160, 160))
            d.text((xs + 2, wf_h + 2), '%d s' % s, font=small, fill=(160, 160, 160))
        for reader, letter, sample in letters:
            column = (sample - win / 2) / hop - start                # the column whose window it is centred in
            if 0 <= column < per:
                y = wf_h + ruler_h + (0 if reader == 'Plain' else letter_h)
                d.text((int(column * COL_PX) - 4, y + 2), letter, font=font,
                       fill=(255, 255, 255) if reader == 'Plain' else (255, 210, 0))
        f = os.path.join(out, '%s_%04ds.png' % (name, int(t0)))
        img.save(f); made.append(f)
    print('%d pictures in %s' % (len(made), out))
    print(texts)


if __name__ == '__main__':
    main(*sys.argv[1:])
