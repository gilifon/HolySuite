"""
THE HONEST TEST: W1AW's BULLETIN, WHOSE TEXT ARRL PUBLISHES WORD FOR WORD.

The twelve-recording bench counts 34 words, and only words two decoders already agreed on - so it
measures the easy part and a difference of one or two words is luck. This measures both decoders
against hundreds of words of real, faded, noisy CW off the air whose exact text is known in advance,
which is as fair a test as this project can get.

For each recording and each decoder it reports:
  read     - how many of the bulletin's words came out right, of those actually transmitted in that
             stretch (the recording covers only part of the bulletin, so the count is taken between
             the first and last word matched)
  wrong    - words the decoder produced that are not in the text at all
Aligned with difflib on the word lists, so a missed word shifts nothing.

  ScoreBulletin.py reference.txt features.bin weights.net wavfolder
"""

import difflib
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import torch  # noqa: E402

from TrainLetters import LetterNet, read, greedy  # noqa: E402
from LookAtAnswers import load  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
QUIET = os.path.join(ROOT, "build", "Quiet.exe")


def words(text):
    return re.findall(r"[A-Z0-9]+", text.upper())


def plain_text(wav):
    """
    Quiet.exe prints one line per tenth of a second with whatever was decoded in it at the end, so
    the pieces are JOINED WITH NOTHING. Joining them with a space - the first version of this - put a
    space every tenth of a second, chopping every word of the plain decoder's output into single
    letters and making its score meaningless against a word list.
    """
    out = subprocess.run([QUIET, wav], capture_output=True, text=True).stdout
    return "".join(line.split("  ")[-1] for line in out.splitlines())


def compare(reference, said):
    a, b = words(reference), words(said)
    if not b:
        return 0, 0, 0
    match = difflib.SequenceMatcher(None, a, b, autojunk=False)
    blocks = [bl for bl in match.get_matching_blocks() if bl.size]
    if not blocks:
        return 0, 0, len(b)
    read = sum(bl.size for bl in blocks)
    spanned = (blocks[-1].a + blocks[-1].size) - blocks[0].a      # reference words actually covered
    return read, spanned, len(b) - read


def main(reference_file, features_file, weights, folder):
    reference = open(reference_file, encoding="utf-8").read()
    print("reference: %d words" % len(words(reference)))

    net_text = {}
    if features_file and os.path.exists(features_file):
        examples, bins = read(features_file)
        model = LetterNet(bins)
        load(model, weights)
        model.eval()
        with torch.no_grad():
            for name, _, d, _ in examples:
                net_text[name] = greedy(model(torch.from_numpy(d).unsqueeze(0))[0])

    totals = {"plain": [0, 0, 0], "network": [0, 0, 0]}
    print("\n%-30s %-9s %5s %5s %6s   %s" % ("recording", "decoder", "read", "of", "wrong", "sample"))
    for wav in sorted(os.listdir(folder)):
        if not wav.endswith(".wav"):
            continue
        name = os.path.splitext(wav)[0]
        for label, said in (("plain", plain_text(os.path.join(folder, wav))),
                            ("network", net_text.get(name, ""))):
            if not said.strip():
                continue
            got, spanned, wrong = compare(reference, said)
            totals[label][0] += got
            totals[label][1] += spanned
            totals[label][2] += wrong
            print("%-30s %-9s %5d %5d %6d   %s" % (name, label, got, spanned, wrong, " ".join(words(said)[:6])))

    print()
    for label in ("plain", "network"):
        got, spanned, wrong = totals[label]
        if spanned:
            print("%-8s read %d of %d words transmitted (%.1f%%), and produced %d words that were never sent"
                  % (label, got, spanned, 100.0 * got / spanned, wrong))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4])
