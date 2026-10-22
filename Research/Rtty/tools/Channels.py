"""Totals a RttyBench summary log by channel: callsigns copied, one column per decoder setting.

  python -I Channels.py <log>
"""
import sys

rows = {}
header = None
for line in open(sys.argv[1]):
    p = line.split()
    if line.startswith("channel"):
        header = line.split("snr", 1)[1].strip()
    if len(p) > 3 and p[0] in ("awgn", "flutter", "moderate", "offset", "poor", "qrm", "gaps"):
        vals = [int(x.split("/")[0]) for x in p[2::2]]
        rows[p[0]] = [a + b for a, b in zip(rows.get(p[0], [0] * len(vals)), vals)]
print(header)
for k, v in rows.items():
    print("%-10s %s" % (k, "  ".join("%5d" % x for x in v)))
print("%-10s %s" % ("TOTAL", "  ".join("%5d" % x for x in [sum(c) for c in zip(*rows.values())])))
