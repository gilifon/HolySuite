"""
Scores decoded text against W1AW's published bulletin.

  ScoreText.py reference.txt decoded.txt [--detail]

decoded.txt is what BulletinText.exe prints: one line per recording, "name<TAB>text".

READ is how many of the bulletin's words came out right, counted only over the stretch each
recording actually covers - the first to the last word matched - since a ten-minute piece holds only
part of the bulletin. INVENTED is words the decoder produced that are not in the text at that place.
Matching is by difflib over word lists, so one missed word does not throw the rest out of step.
"""

import difflib
import re
import sys


def words(text):
    return re.findall(r"[A-Z0-9]+", text.upper())


def compare(reference_words, said):
    b = words(said)
    if not b:
        return 0, 0, 0
    blocks = [bl for bl in difflib.SequenceMatcher(None, reference_words, b, autojunk=False).get_matching_blocks() if bl.size]
    if not blocks:
        return 0, 0, len(b)
    read = sum(bl.size for bl in blocks)
    spanned = (blocks[-1].a + blocks[-1].size) - blocks[0].a
    return read, spanned, len(b) - read


def main(reference_file, decoded_file, detail=False):
    reference_words = words(open(reference_file, encoding="utf-8").read())
    total_read = total_span = total_invented = 0

    for line in open(decoded_file, encoding="utf-8"):
        if "\t" not in line:
            continue
        name, said = line.rstrip("\n").split("\t", 1)
        read, spanned, invented = compare(reference_words, said)
        total_read += read
        total_span += spanned
        total_invented += invented
        if detail:
            print("  %-30s read %4d of %4d   invented %4d" % (name, read, spanned, invented))

    share = 100.0 * total_read / total_span if total_span else 0.0
    print("READ %d of %d words (%.1f%%)   INVENTED %d" % (total_read, total_span, share, total_invented))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], "--detail" in sys.argv)
