"""
Scores decoded text against W1AW's published bulletin.

  ScoreText.py reference.txt decoded.txt [--detail]

decoded.txt is what BulletinText.exe prints: one line per recording, "name<TAB>text".

READ is how many of the bulletin's words came out right, counted only over the stretch each
recording actually covers - the first to the last word matched - since a ten-minute piece holds only
part of the bulletin. INVENTED is words the decoder produced that are not in the text at that place.
Matching is a local alignment over word lists, so one missed word does not throw the rest out of step.
"""

import re
import sys


GAP = 0.1


def words(text):
    return re.findall(r"[A-Z0-9]+", text.upper())


def compare(reference_words, said):
    # Best-scoring local alignment: a right word +1, a wrong, missing or extra word -0.1. difflib's greedy longest block
    # lost a whole part when the same text was sent several times (4z5sl: part 3 scored 1 of 33 words though the decoder
    # read it); a plain longest-common-subsequence went the other way and let lone junk "K"s and "DE"s collect far apart.
    a, b = reference_words, words(said)
    if not b:
        return 0, 0, 0
    n, m = len(a), len(b)
    H = [[0.0] * (m + 1) for _ in range(n + 1)]
    back = [[0] * (m + 1) for _ in range(n + 1)]             # 1 diagonal, 2 up, 3 left, 0 start
    best = bi = bj = 0
    for i in range(1, n + 1):
        Hi, Hp, Bi, ai = H[i], H[i - 1], back[i], a[i - 1]
        for j in range(1, m + 1):
            d = Hp[j - 1] + (1.0 if ai == b[j - 1] else -GAP)
            u = Hp[j] - GAP
            l = Hi[j - 1] - GAP
            v = max(d, u, l, 0.0)
            Hi[j] = v
            Bi[j] = 0 if v == 0.0 else 1 if v == d else 2 if v == u else 3
            if v > best:
                best, bi, bj = v, i, j
    if best == 0:
        return 0, 0, m
    i, j = bi, bj
    read = 0
    first = last = -1
    while i > 0 and j > 0 and back[i][j]:
        t = back[i][j]
        if t == 1:
            if a[i - 1] == b[j - 1]:
                read += 1
                if last < 0:
                    last = i - 1
                first = i - 1
            i -= 1
            j -= 1
        elif t == 2:
            i -= 1
        else:
            j -= 1
    return read, last - first + 1, m - read


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
