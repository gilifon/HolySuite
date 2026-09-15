"""
DO THE LABELS LINE UP WITH THE SOUND?

The single thing that would silently ruin the training. The labels are laid out by arithmetic - so
many steps per dit - while the envelope comes from the real front end working in whole blocks of
audio. If the two drift apart by even a few steps the network is taught that the tone is on when it
is off, and no amount of training fixes it.

The check needs no truth beyond what we already have: where a label says an element is in progress
the envelope should be loud, and where it says a character or word has ended it should be quiet.
Measured on the strongest examples only - on a weak one the envelope is genuinely noise, and that
proves nothing either way.

Prints the average envelope under each of the seven labels. Element labels should stand well clear
of the two silence labels. If they do not, the alignment is wrong.
"""

import sys


def main(path):
    under = [[] for _ in range(7)]
    shown = 0

    for line in open(path, encoding="utf-8"):
        bits = line.rstrip("\n").split("\t")
        if len(bits) < 4:
            continue

        env = [float(v) for v in bits[2].split()]
        lab = [int(v) for v in bits[3].split()]
        if len(env) != len(lab):
            print("LENGTH MISMATCH in %s: %d envelope, %d labels" % (bits[0], len(env), len(lab)))
            continue

        # Only the examples loud enough to judge. On a weak one the tone IS noise.
        if max(env) < 0.9:
            continue

        for e, l in zip(env, lab):
            under[l].append(e)

        if shown < 2:
            shown += 1
            print("%s  %s" % (bits[0], bits[1]))
            print("  labels   " + "".join(str(l) for l in lab[:110]))
            print("  envelope " + "".join(" .:-=+*#%@"[min(9, int(e * 9.999))] for e in env[:110]))
            print()

    names = ["char ended", "word ended", "element 1", "element 2", "element 3", "element 4", "element 5"]
    print("average envelope under each label, on the strong examples only:")
    for i in range(7):
        if under[i]:
            print("  %-11s %6.3f   (%d steps)" % (names[i], sum(under[i]) / len(under[i]), len(under[i])))
        else:
            print("  %-11s never used" % names[i])


if __name__ == "__main__":
    main(sys.argv[1])
