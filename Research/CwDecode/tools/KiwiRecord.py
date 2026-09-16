"""
RECORD A PUBLIC KIWISDR TO A WAV FILE THE DECODER CAN READ.

Why not kiwiclient's own kiwirecorder: it opens "<host>:<port>/<stamp>/SND", and receivers on
firmware 1.902 answer that with silence and hang up after ten seconds - measured on several. Their
own web page opens "kiwi/<stamp>/SND", and on that path the receiver talks at once. So this is a
small recorder on the path that works, and nothing more.

TUNED AS USB WITH THE DIAL 500 Hz BELOW THE STATION, so the station sounds at 500 Hz in a 200-900 Hz
passband. Not the receiver's "cw" mode: whether that mode's frequency means the carrier or the dial
is exactly the kind of thing that silently puts the signal out of the passband, and USB with a stated
dial leaves no doubt where the tone lands.

Audio arrives uncompressed as 16-bit big-endian samples at the receiver's own rate, about 12000 Hz,
and is written at 8000 Hz - what the IC-7610 recordings use - so every tool here reads it unchanged.

  KiwiRecord.py host port station_kHz seconds out.wav
"""

import struct
import sys
import time
import wave

import numpy as np
import websocket
from scipy.signal import resample_poly


def record(host, port, station_khz, seconds, out):
    dial = station_khz - 0.5
    ws = websocket.create_connection("ws://%s:%s/kiwi/%d/SND" % (host, port, int(time.time())), timeout=15)

    verbose = bool(int(__import__("os").environ.get("KIWI_VERBOSE", "0")))

    def say(text):
        if verbose:
            print("  >> " + text)
        ws.send(text)

    # IN THE RECEIVER'S OWN ORDER, taken from kiwiclient: log in; when it announces audio_rate,
    # acknowledge it; when it announces sample_rate, THEN tune and set the AGC; and a keepalive once a
    # second. Sending the tuning straight after the login - the first version - got the connection
    # dropped part way through.
    say("SET auth t=kiwi p=")

    rate = 12000.0
    chunks = []
    tuned = False
    started = time.time()
    last_keepalive = 0
    while time.time() - started < seconds + 5:
        if tuned and int(time.time()) != last_keepalive:
            say("SET keepalive")
            last_keepalive = int(time.time())
        try:
            frame = ws.recv()
        except websocket.WebSocketTimeoutException:
            break
        except websocket.WebSocketConnectionClosedException:
            print("  the receiver closed the connection after %.1f s, %d audio blocks kept" % (time.time() - started, len(chunks)))
            break
        if verbose and isinstance(frame, bytes) and frame[:3] == b"MSG":
            print("  << " + frame[4:120].decode("latin-1", "replace"))
        if isinstance(frame, str):
            continue
        tag = frame[:3]
        if tag == b"MSG":
            text = frame[4:].decode("latin-1", "replace")
            for part in text.split(" "):
                if part.startswith("audio_rate="):
                    say("SET AR OK in=%d out=44100" % int(float(part.split("=")[1])))
                elif part.startswith("sample_rate=") and not tuned:
                    rate = float(part.split("=")[1])
                    say("SET squelch=0 max=0")
                    say("SET genattn=0")
                    say("SET gen=0 mix=-1")
                    say("SET ident_user=HolyLogger-CW-research")
                    say("SET mod=usb low_cut=200 high_cut=900 freq=%.3f" % dial)
                    say("SET agc=1 hang=0 thresh=-100 slope=6 decay=1000 manGain=50")
                    say("SET compression=0")
                    say("SET keepalive")
                    tuned = True
                    started = time.time()     # the recording's length counts from here
                # These arrive on every connection with a value; only a non-zero one is a refusal.
                # (too_busy=0 is the receiver saying it is NOT busy.)
                elif "=" in part and part.split("=")[0] in ("too_busy", "down", "badp") and part.split("=")[1] not in ("", "0"):
                    raise SystemExit("receiver refused: " + part)
                elif part.startswith("redirect=") and len(part) > len("redirect="):
                    raise SystemExit("receiver refused: " + part)
        elif tag == b"SND" and tuned:
            if time.time() - started > seconds:
                break
            # "SND", one byte of flags, four bytes of sequence, two bytes of S-meter, then the samples.
            # Bit 0x10 set means ADPCM-compressed; compression was switched off, so a compressed block
            # can only be one sent before the switch took effect, and it is skipped rather than
            # misread as samples.
            body = frame[3:]
            if len(body) > 7 and not (body[0] & 0x10):
                chunks.append(np.frombuffer(body[7:], dtype=">i2").astype(np.float64))
    ws.close()

    if not chunks:
        raise SystemExit("no audio received")
    audio = np.concatenate(chunks)
    eight = resample_poly(audio, 8000, int(round(rate)))
    eight = np.clip(eight, -32768, 32767).astype("<i2")
    with wave.open(out, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(8000)
        w.writeframes(eight.tobytes())
    print("%s: %.1f s at %.0f Hz -> %s" % (host, len(audio) / rate, rate, out))


if __name__ == "__main__":
    record(sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4]), sys.argv[5])
