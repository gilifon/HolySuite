"""
READING THE NETWORK'S ANSWER MORE CLEVERLY (2026-10-26). The network gives, for every 15 ms step, how likely
each letter (and the CTC blank) is. Until now we took the single most likely one at every step ("greedy"),
which throws away the second choices - "R3ZBS" when the I was a close second.

This is a CTC prefix beam search: it keeps the few most likely spellings so far, step by step, and when a
spelling closes a word (a word gap, or the end) it gets a bonus if the word is a KNOWN one:
  - the CW words and abbreviations of a contact (CQ DE K TU 5NN RST QTH 73 ...),
  - common English words (from Python's own documentation, as gen.py's practice text - never the test texts),
  - anything shaped like a callsign.
ALPHA is the bonus in log-probability: 0 is plain beam search.

  beam_decode(lp, beam=8, alpha=1.0) -> [(char, step)]      lp: [T, C] log-probabilities, numpy
"""
import math
import os
import re
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from common import CHARS, BLANK

SPACE = CHARS.index(' ')
CW_WORDS = set("""
CQ DE K KN BK AR SK AS BT TU TNX TKS THX TNK 73 88 72 UR URS RST 599 5NN 579 559 589 569 479 339 449 ES FB OM YL XYL HR HW HRD
CPY CPI NAME OP QTH RIG ANT PWR WX TEMP GM GA GE GN GD SRI AGN PSE PLS QSL QSO QRZ QRM QRN QSB QSY QRP QRO QRL QRS QRQ QRT
QRV QRX QRG QSK QSP QTR CUL GL GUD DX TEST NR CFM ABT ALL BEST DR FER FOR MNI TNX VY VERY NICE QSO RPT RPRT INFO LOOG LOG
WKD WRK SIGS SIG BAND COND CONDX UP DN DWN RX TX TRX KW W WATTS WATT DIPOLE YAGI VERT BEAM LP SP POTA SOTA WWFF IOTA
""".split())

CALL_RE = re.compile(r'^(?:[A-Z0-9]{1,4}/)?(?:[A-Z]{1,2}|[0-9][A-Z]{1,2}|[A-Z][0-9])[0-9][A-Z]{1,4}(?:/[A-Z0-9]{1,3})?$')

_english = None


def english():
    """The 4000 commonest words of Python's documentation, upper case, 2-11 letters."""
    global _english
    if _english is None and os.environ.get('WORDLIST'):
        # another list to try (one word a line), e.g. tools/HunspellWords.py's
        _english = set(w.strip().upper() for w in open(os.environ['WORDLIST'], encoding='utf-8') if w.strip())
    if _english is None:
        topics = os.path.join(sys.base_prefix, 'Lib', 'pydoc_data', 'topics.py')
        words = {}
        try:
            text = open(topics, encoding='utf-8', errors='ignore').read().upper()
            for w in re.findall(r'[A-Z]{2,11}', text):
                words[w] = words.get(w, 0) + 1
        except OSError:
            pass
        _english = set(w for w, n in sorted(words.items(), key=lambda kv: -kv[1])[:4000])
    return _english


def known(word):
    return bool(word) and (word in CW_WORDS or word in english() or CALL_RE.match(word) is not None
                           or (word.isdigit() and len(word) <= 4))


def _lse(a, b):
    if a == -math.inf:
        return b
    if b == -math.inf:
        return a
    m = max(a, b)
    return m + math.log(math.exp(a - m) + math.exp(b - m))


def beam_decode(lp, beam=8, alpha=1.0, prune=-7.0):
    """CTC prefix beam search with a known-word bonus. -> [(char, step it was first said)]."""
    T, C = lp.shape
    # a beam: prefix (string) -> [log p ending in blank, log p ending in its last letter, bonus so far, times]
    beams = {'': [0.0, -math.inf, 0.0, ()]}

    def word_bonus(prefix):
        # the word just closed: the letters since the previous space
        word = prefix.rstrip(' ').rsplit(' ', 1)[-1]
        return alpha if known(word) else 0.0

    for t in range(T):
        row = lp[t]
        cands = [k for k in range(C) if row[k] > prune or k == BLANK]
        nxt = {}

        def get(p, bonus, times):
            e = nxt.get(p)
            if e is None:
                e = nxt[p] = [-math.inf, -math.inf, bonus, times]
            return e

        for prefix, (pb, pnb, bonus, times) in beams.items():
            total = _lse(pb, pnb)
            last = prefix[-1] if prefix else None
            for k in cands:
                p = float(row[k])
                if k == BLANK:
                    e = get(prefix, bonus, times)
                    e[0] = _lse(e[0], total + p)
                    continue
                ch = CHARS[k]
                if ch == ' ' and (not prefix or last == ' '):
                    # a word gap with no word before it says nothing new: held as the same prefix
                    e = get(prefix, bonus, times)
                    e[1] = _lse(e[1], total + p)
                    continue
                if ch == last:
                    # the same letter again: only after a blank is it a new one; else it is the same one held
                    e = get(prefix, bonus, times)
                    e[1] = _lse(e[1], pnb + p)
                    np_ = prefix + ch
                    e2 = get(np_, bonus, times + (t,))
                    e2[1] = _lse(e2[1], pb + p)
                    continue
                np_ = prefix + ch
                b = bonus + (word_bonus(prefix) if ch == ' ' else 0.0)
                e = get(np_, b, times + (t,))
                e[1] = _lse(e[1], total + p)
        # keep the best, by probability plus the bonus earned
        beams = dict(sorted(nxt.items(), key=lambda kv: -(_lse(kv[1][0], kv[1][1]) + kv[1][2]))[:beam])

    def final(kv):
        prefix, (pb, pnb, bonus, times) = kv
        tail = word_bonus(prefix) if prefix and prefix[-1] != ' ' else 0.0
        return _lse(pb, pnb) + bonus + tail

    best_prefix, (_, _, _, best_times) = max(beams.items(), key=final)
    return list(zip(best_prefix, best_times))
