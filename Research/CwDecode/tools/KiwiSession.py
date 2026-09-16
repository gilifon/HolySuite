"""
A LONG RECORDING FROM ONE KIWISDR, IN PIECES.

A public receiver can drop a listener at any moment - its owner restarts it, the internet hiccups,
someone with a better claim takes the slot. One hour-long file would lose everything after the first
drop, so this records in segments of a few minutes each, one file each, and simply starts the next
segment when one ends early. What is lost is the gap, not the evening.

  KiwiSession.py host port station_kHz total_seconds segment_seconds out_prefix
"""

import os
import subprocess
import sys
import time


def main(host, port, khz, total, segment, prefix):
    here = os.path.dirname(os.path.abspath(__file__))
    ends = time.time() + float(total)
    part = 0
    while time.time() < ends - 5:
        part += 1
        length = min(float(segment), ends - time.time())
        stamp = time.strftime("%H%M%S", time.gmtime())
        out = "%s_%02d_%sZ.wav" % (prefix, part, stamp)
        started = time.time()
        result = subprocess.run([sys.executable, os.path.join(here, "KiwiRecord.py"), host, port, khz, str(int(length)), out],
                                capture_output=True, text=True)
        took = time.time() - started
        print("%s part %d: %.0f s  %s" % (host, part, took, (result.stdout.strip() or result.stderr.strip()[-120:])))
        sys.stdout.flush()
        if took < 5:
            time.sleep(20)          # refused outright: wait before asking again


if __name__ == "__main__":
    main(*sys.argv[1:7])
