"""
REAL CW, LABELLED LETTER BY LETTER, FOR THE LETTER NETWORK TO LEARN FROM.

The network has only ever been taught on CW this project generated. W1AW's bulletins are real CW off
the air - fading, noise, other stations, internet receivers' own audio - and ARRL publishes the text.
What is missing is WHEN each letter was sent, which the network needs: it is taught by naming each
letter in the gap just after it.

HOW THE TIMES ARE FOUND, and why the weak copies can be labelled at all:
  1. LetterTimes.ps1 gives, for every recording, each letter the plain decoder spelled and the sample
     where it ended.
  2. Each recording's letters are lined up against the published text. The recording of each part
     that matches best - a strong, clean copy - becomes that part's clock.
  3. Every receiver heard the SAME transmission at the same moment. So a weak, fading copy is put on
     that clock by the letters it did read: the median difference in time between its letters and the
     clock's for the same letters of the text is its offset. It then inherits the clock copy's times
     for every letter - including the ones it could not read itself, which are exactly the ones worth
     learning from.
  4. The text is cut into pieces of about twelve seconds, at word gaps, keeping only stretches where
     the clock copy placed every letter (a run of up to three letters it missed is filled in by
     spacing them out by their Morse length between the letters either side; anything longer is left
     out). Each piece is written as its own short wav for every receiver, with its marks.

THE LABELS follow MakeCtcAudio exactly: a letter is named from 1.5 to 2.3 dits after its last element,
a word gap from 3.3 to 3.9 dits after the last letter of the word. The dit is measured per part from
the clock copy: time between letters against their length in Morse units.

  LabelReal.py times.txt reference.txt wavfolder outfolder [prefix]

Writes outfolder/<prefix>_<rx>_<part>_<n>.wav and appends to outfolder/index.txt one line each:
name<TAB>text<TAB><TAB><TAB><TAB><TAB>marks - the layout DumpFeatures.exe reads.
"""

import difflib
import os
import re
import sys
import wave
from collections import defaultdict

import numpy as np

MORSE = {
    'A': '.-', 'B': '-...', 'C': '-.-.', 'D': '-..', 'E': '.', 'F': '..-.', 'G': '--.', 'H': '....', 'I': '..',
    'J': '.---', 'K': '-.-', 'L': '.-..', 'M': '--', 'N': '-.', 'O': '---', 'P': '.--.', 'Q': '--.-', 'R': '.-.',
    'S': '...', 'T': '-', 'U': '..-', 'V': '...-', 'W': '.--', 'X': '-..-', 'Y': '-.--', 'Z': '--..',
    '0': '-----', '1': '.----', '2': '..---', '3': '...--', '4': '....-', '5': '.....', '6': '-....',
    '7': '--...', '8': '---..', '9': '----.', '/': '-..-.', '=': '-...-', '?': '..--..', '.': '.-.-.-',
    ',': '--..--', "'": '.----.',
}
RATE = 8000
PIECE_SECONDS = 12.0
LONGEST_GUESSED_RUN = 3


def units(ch):
    code = MORSE[ch]
    return sum(1 if s == '.' else 3 for s in code) + len(code) - 1


def reference_letters(path):
    """The published text as (letter, word index) - only characters Morse can carry."""
    text = open(path, encoding='utf-8', errors='replace').read().upper()
    text = re.sub(r'<[^>]*>', ' ', text)
    # W1AW SENDS A HYPHEN AS THE WORD "DASH" - "C-CLASS" goes out as C DASH CLASS, heard in every
    # recording. Read literally, the hyphen vanished and the four letters D A S H were in the sound
    # with no label on them, teaching the network to say nothing for real letters.
    text = text.replace('-', ' DASH ')
    out = []
    for w, word in enumerate(text.split()):
        for ch in word:
            if ch in MORSE:
                out.append((ch, w))
    return out


def read_times(path):
    rec = {}
    for line in open(path, encoding='utf-8'):
        if '\t' not in line:
            continue
        name, body = line.rstrip('\n').split('\t', 1)
        items = []
        for tok in body.split():
            ch, _, at = tok.rpartition('@')
            if ch and at.isdigit():
                items.append((ch, int(at)))
        rec[name] = items
    return rec


def align(ref, heard):
    """{reference index: sample} for every letter heard that lines up with the text."""
    a = ''.join(ch for ch, _ in ref)
    # One character per letter heard: a prosign such as <AS> is one letter but four characters, and
    # it is never in the published text anyway.
    b = ''.join(ch if len(ch) == 1 else '#' for ch, _ in heard)
    got = {}
    for block in difflib.SequenceMatcher(None, a, b, autojunk=False).get_matching_blocks():
        if block.size < 3:          # short matches are chance, not the text
            continue
        for k in range(block.size):
            got[block.a + k] = heard[block.b + k][1]
    return got


def ms_per_unit(ref, clock):
    """Samples per Morse unit, from the time between neighbouring letters the clock placed."""
    ratios = []
    keys = sorted(clock)
    for i, j in zip(keys, keys[1:]):
        if j != i + 1:
            continue
        gap_units = units(ref[j][0]) + (7 if ref[j][1] != ref[i][1] else 3)
        ratios.append((clock[j] - clock[i]) / gap_units)
    return float(np.median(ratios)) if ratios else None


def fill(ref, clock, spu):
    """Every reference letter the clock can place, short misses filled in by Morse length."""
    placed = dict(clock)
    keys = sorted(clock)
    for i, j in zip(keys, keys[1:]):
        if j - i <= 1 or j - i - 1 > LONGEST_GUESSED_RUN:
            continue
        # Units from letter i's end to each missing letter's end, scaled to the measured interval.
        total, marks = 0.0, []
        for k in range(i + 1, j + 1):
            total += units(ref[k][0]) + (7 if ref[k][1] != ref[k - 1][1] else 3)
            marks.append(total)
        span = clock[j] - clock[i]
        if span <= 0:
            continue
        for k, m in zip(range(i + 1, j), marks[:-1]):
            placed[k] = clock[i] + span * m / total
    return placed


def pieces(ref, placed, spu):
    """Runs of whole words, every letter placed, about PIECE_SECONDS long: (first, last) indices."""
    out = []
    n = len(ref)
    i = 0
    while i < n:
        # start at the first letter of a word whose letters are all placed
        if i not in placed or (i > 0 and ref[i - 1][1] == ref[i][1]):
            i += 1
            continue
        j = i
        start_time = placed[i]
        last_good_word_end = None
        while j < n and j in placed:
            word_ends = j + 1 >= n or ref[j + 1][1] != ref[j][1]
            if word_ends:
                last_good_word_end = j
                if placed[j] - start_time >= PIECE_SECONDS * RATE:
                    break
            j += 1
        if last_good_word_end is not None and last_good_word_end > i:
            out.append((i, last_good_word_end))
            i = last_good_word_end + 1
        else:
            i = j + 1
    return out


def main(times_file, reference_file, folder, outdir, prefix='w1aw'):
    ref = reference_letters(reference_file)
    times = read_times(times_file)
    os.makedirs(outdir, exist_ok=True)
    index = open(os.path.join(outdir, 'index.txt'), 'a', encoding='utf-8')

    parts = defaultdict(list)                      # "02" -> [recording names]
    for name in times:
        m = re.match(r'(.+?)_(\d\d)_\d+Z$', name)
        if m:
            parts[m.group(2)].append(name)

    written = 0
    for part, names in sorted(parts.items()):
        aligned = {n: align(ref, times[n]) for n in names}
        best = max(names, key=lambda n: len(aligned[n]))
        clock = aligned[best]
        if len(clock) < 100:
            print('part %s: nothing to learn from (best copy placed %d letters)' % (part, len(clock)))
            continue
        spu = ms_per_unit(ref, clock)
        placed = fill(ref, clock, spu)
        cuts = pieces(ref, placed, spu)
        print('part %s: clock %s placed %d letters, %.1f ms a unit (%.1f WPM), %d pieces'
              % (part, best, len(clock), spu / 8, 1200 / (spu / 8), len(cuts)))

        for name in names:
            common = [placed[k] - 0 for k in aligned[name] if k in clock]
            offsets = [aligned[name][k] - clock[k] for k in aligned[name] if k in clock]
            if len(offsets) < 20:
                print('    %-28s too few letters in common (%d) - left out' % (name, len(offsets)))
                continue
            offset = float(np.median(offsets))
            spread = float(np.median(np.abs(np.array(offsets) - offset)))
            if spread > 0.5 * spu:
                print('    %-28s letters do not agree on an offset (spread %.0f ms) - left out' % (name, spread / 8))
                continue

            w = wave.open(os.path.join(folder, name + '.wav'))
            audio = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16)
            w.close()

            kept = 0
            for n_piece, (first, last) in enumerate(cuts):
                dit = spu
                begin = placed[first] - units(ref[first][0]) * dit - 4 * dit + offset
                end = placed[last] + 4.5 * dit + offset
                a, b = int(begin), int(end)
                if a < 0 or b > len(audio):
                    continue
                marks = []
                text_words = []
                for k in range(first, last + 1):
                    t = placed[k] + offset - a
                    marks.append('%s@%d+%d' % (ref[k][0], t + 1.5 * dit, 0.8 * dit))
                    word_ends = k == last or ref[k + 1][1] != ref[k][1]
                    if word_ends and k != last:
                        marks.append('_@%d+%d' % (t + 3.3 * dit, 0.6 * dit))
                for k in range(first, last + 1):
                    if k == first or ref[k][1] != ref[k - 1][1]:
                        text_words.append('')
                    text_words[-1] += ref[k][0]
                piece_name = '%s_%s_%02d' % (prefix, name, n_piece)
                o = wave.open(os.path.join(outdir, piece_name + '.wav'), 'wb')
                o.setnchannels(1); o.setsampwidth(2); o.setframerate(RATE)
                o.writeframes(audio[a:b].tobytes())
                o.close()
                index.write('%s\t%s\t\t\t\t\t%s\n' % (piece_name, ' '.join(text_words), ' '.join(marks)))
                kept += 1
                written += 1
            print('    %-28s offset %+6.0f ms  %3d letters in common  %3d pieces' % (name, offset / 8, len(offsets), kept))

    index.close()
    print('%d labelled pieces written' % written)


if __name__ == '__main__':
    main(*sys.argv[1:])
