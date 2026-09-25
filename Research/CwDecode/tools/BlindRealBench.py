"""
RealBench.cs, scored exactly the same way, for the whole-element decoder (ElementBlind / BlindFolder).

His IC-7610 recordings: the words known to be in each (found anywhere in the text, spaces ignored),
and two recordings where the right answer is SILENCE - every twenty letters printed there costs one.
The plain decoder scores 32 of 34 here.

  BlindRealBench.py [recordings folder]        (decodes the twelve in parallel, then scores)
"""

import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

RECORDINGS = [
    ("quiet.wav", "V4TQ working IZ5CMG and SP9ADG", ["V4TQ", "5NN", "IZ5CMG", "SP9ADG"]),
    ("radio.wav", "French QSO, 675 Hz, fast", ["PWR", "SUR", "DELTA", "22MUP", "MERCI", "POURCE", "AUPLAISIR", "SOIR", "BONNE"]),
    ("radio2.wav", "about 19 WPM ragchew", ["RIGRIG", "80W"]),
    ("narrow.wav", "LB2WD, radio filter narrowed", ["LB2WD", "DERA1QN", "73GL"]),
    ("bad.wav", "same QSO, four stations in the passband", ["LB2WD", "DERA1Q"]),
    ("ly2px2.wav", "LY2PX, fast and weak", ["K1Y", "TOMEET", "AGN"]),
    ("ly2px.wav", "LY2PX calling CQ", ["LY2PX", "CQCQCQDE"]),
    ("slow.wav", "IU5RDL calling CQ at 15 WPM", ["IU5RDL", "CQCQ", "PSE"]),
    ("weak.wav", "weak F4A.. working VK6, 24 WPM", ["GUD", "HIHI", "AGN"]),
    ("active.wav", "R1LN, busy frequency", ["R1LN", "DER1LN", "7388"]),
    ("beacons.wav", "14.100, three unreadable beacons - stay QUIET", []),
    ("session1.wav", "eight minutes of a near-empty band - stay QUIET", []),
]


def main(folder=os.path.join(HERE, "..", "recordings")):
    build = os.path.join(HERE, "..", "build")
    py = sys.executable
    procs = []
    for f, _, _ in RECORDINGS:
        out = os.path.join(build, "rb_" + f[:-4] + ".txt")
        procs.append(subprocess.Popen([py, os.path.join(HERE, "BlindFolder.py"), folder, out, f[:-4]],
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
    for p in procs:
        p.wait()

    found = wanted = noise = 0
    for f, what, expected in RECORDINGS:
        out = os.path.join(build, "rb_" + f[:-4] + ".txt")
        text = ""
        for line in open(out, encoding="utf-8"):
            name, _, t = line.rstrip("\n").partition("\t")
            if name == f[:-4]:
                text = t
        flat = text.replace(" ", "")
        if not expected:
            noise += len(flat) // 20
            print("%-12s %-46s %d letters of noise (want 0)" % (f, what, len(flat)))
        else:
            got = [w for w in expected if w in flat]
            found += len(got); wanted += len(expected)
            missed = [w for w in expected if w not in got]
            print("%-12s %-46s %d of %d%s" % (f, what, len(got), len(expected), ("   missed: " + " ".join(missed)) if missed else ""))
        print("             " + text[:160])
    print("found %d of %d, noise marks %d  =>  %d" % (found, wanted, noise, found - noise))


if __name__ == "__main__":
    main(*sys.argv[1:])
