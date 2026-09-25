"""
WHAT A FISTKEYER RUN ACTUALLY SENT - per part, measured from edges.tsv, not from the plan.

For every part: the speed (from the median dit), the dah/dit ratio, and how much the dits, dahs and
gaps scatter. Also how far the keying thread's actual edges fell from the planned ones - the labels
are only as good as that number.

  FistStats.py <outdir>
"""

import csv
import os
import statistics
import sys


def main(outdir):
    rows = list(csv.DictReader(open(os.path.join(outdir, "edges.tsv")), delimiter="\t"))
    sent = open(os.path.join(outdir, "SENT.txt")).read().splitlines()
    worst = 0.0
    for part in sorted({int(r["part"]) for r in rows}):
        pr = [r for r in rows if int(r["part"]) == part]
        lens = [float(r["actual_up_ms"]) - float(r["actual_down_ms"]) for r in pr if r["actual_down_ms"] != "nan"]
        if not lens:
            continue
        for r in pr:
            if r["actual_down_ms"] != "nan":
                worst = max(worst, abs(float(r["actual_down_ms"]) - float(r["down_ms"])),
                            abs(float(r["actual_up_ms"]) - float(r["up_ms"])))
        short = sorted(l for l in lens if l < 2 * min(lens) + 10)
        dit = statistics.median(short)
        dits = [l for l in lens if l < 2 * dit]
        dahs = [l for l in lens if l >= 2 * dit]
        gaps = [float(pr[i + 1]["actual_down_ms"]) - float(pr[i]["actual_up_ms"]) for i in range(len(pr) - 1)
                if pr[i]["text_index"] == pr[i + 1]["text_index"] and pr[i + 1]["actual_down_ms"] != "nan"]
        sd = lambda xs: (statistics.pstdev(xs) / statistics.mean(xs) * 100) if len(xs) > 1 else 0
        print("part %d: %4.1f WPM  dah/dit %.2f  scatter dit %2.0f%% dah %2.0f%% gap %2.0f%%  %s"
              % (part + 1, 1200 / dit, statistics.median(dahs) / dit if dahs else 0,
                 sd(dits), sd(dahs), sd(gaps), sent[part][:60]))
    print("worst planned-vs-actual edge: %.2f ms" % worst)


if __name__ == "__main__":
    main(sys.argv[1])
