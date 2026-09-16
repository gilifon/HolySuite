"""Connect to one KiwiSDR, log in as a listener, and print the first things it says back."""

import sys
import time

import websocket  # websocket-client


def main(host, port):
    path = sys.argv[3] if len(sys.argv) > 3 else "%d/SND"
    url = ("ws://%s:%s/" % (host, port)) + (path % int(time.time()))
    ws = websocket.create_connection(url, timeout=10)
    print("connected to " + url)
    ws.send("SET auth t=kiwi p=")
    started = time.time()
    while time.time() - started < 8:
        try:
            frame = ws.recv()
        except websocket.WebSocketTimeoutException:
            print("  (nothing for 10 s)")
            break
        if isinstance(frame, bytes):
            print("  %-4s %d bytes  %s" % (frame[:3].decode("latin-1"), len(frame), frame[3:80].decode("latin-1", "replace").replace("\n", " ")))
        else:
            print("  text: " + frame[:100])
    ws.close()


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
