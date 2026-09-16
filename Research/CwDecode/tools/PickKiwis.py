"""
Which public KiwiSDRs are placed to hear W1AW?

Reads the public receiver list and ranks the ones that are online, have a free listening slot and
cover HF, by distance from W1AW in Newington, Connecticut. Around 22:00 UTC forty metres lands best
roughly 600 to 1500 km away and twenty metres further out, so both rings are listed.
"""

import json
import math
import re
import sys

W1AW = (41.7148, -72.7272)


def km(a, b):
    la1, lo1, la2, lo2 = map(math.radians, (a[0], a[1], b[0], b[1]))
    h = math.sin((la2 - la1) / 2) ** 2 + math.cos(la1) * math.cos(la2) * math.sin((lo2 - lo1) / 2) ** 2
    return 6371 * 2 * math.asin(math.sqrt(h))


def main(path):
    raw = open(path, encoding="utf-8", errors="replace").read()
    body = raw[raw.index("["):raw.rindex("]") + 1]
    body = re.sub(r",\s*([\]}])", r"\1", body)
    kiwis = json.loads(body)

    rows = []
    for k in kiwis:
        if k.get("offline") != "no" or k.get("status") != "active":
            continue
        try:
            users, most = int(k.get("users", "9")), int(k.get("users_max", "0"))
        except ValueError:
            continue
        if users >= most:
            continue
        m = re.match(r"\(([-\d.]+),\s*([-\d.]+)\)", k.get("gps", ""))
        url = k.get("url", "")
        if not m or not url:
            continue
        d = km(W1AW, (float(m.group(1)), float(m.group(2))))
        snr = k.get("snr", "")
        rows.append((d, k.get("name", "")[:48], url, users, most, snr))

    for label, lo, hi in (("40 m ring", 500, 1600), ("20 m ring", 1600, 3500), ("80 m ring", 150, 600)):
        print("\n" + label)
        for d, name, url, users, most, snr in sorted(r for r in rows if lo <= r[0] <= hi)[:14]:
            print("  %5.0f km  %-48s %-40s users %s/%s  snr %s" % (d, name, url, users, most, snr))


if __name__ == "__main__":
    main(sys.argv[1])
