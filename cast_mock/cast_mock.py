"""Cast an animated mock dashboard to a Chromecast.

Renders frames live, pipes them into ffmpeg as HLS, serves the stream over
HTTP from this machine, and tells the Chromecast to play it using Google's
Default Media Receiver (CC1AD845).

This deliberately avoids the Home Assistant receiver apps (A078F6B0 /
B45F4572), which the devices currently refuse to launch.

Usage:
    python cast_mock.py "Guest room TV"
    python cast_mock.py "Game room TV" --port 823 --fps 10
Stop with Ctrl-C.
"""

import argparse
import functools
import http.server
import os
import shutil
import socket
import socketserver
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime

import pychromecast

from make_dashboard import W, H, render

APP_DEFAULT_MEDIA = "CC1AD845"


def lan_ip(probe="192.168.5.70"):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect((probe, 80))
        return s.getsockname()[0]
    finally:
        s.close()


def serve(directory, port):
    handler = functools.partial(QuietHandler, directory=directory)
    httpd   = ThreadingHTTPServer(("0.0.0.0", port), handler)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    return httpd


class ThreadingHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads      = True
    allow_reuse_address = True


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, fmt, *args):
        # show what the Chromecast actually fetches
        print(f"    HTTP {self.client_address[0]} {fmt % args}", flush=True)

    def end_headers(self):
        # Chromecast is strict about caching + CORS on HLS segments
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        super().end_headers()


def start_ffmpeg(outdir, fps):
    playlist = os.path.join(outdir, "stream.m3u8")
    cmd = [
        "ffmpeg", "-loglevel", "error", "-y",
        "-f", "rawvideo", "-pix_fmt", "rgb24",
        "-s", f"{W}x{H}", "-r", str(fps), "-i", "pipe:0",
        # Chromecast reliably wants an audio track present in the TS
        "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
        "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
        "-profile:v", "high", "-level", "4.0",
        "-pix_fmt", "yuv420p", "-g", str(fps * 2), "-sc_threshold", "0",
        "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "2",
        "-f", "hls",
        "-hls_time", "2",
        "-hls_list_size", "6",
        "-hls_flags", "delete_segments+independent_segments+omit_endlist",
        "-hls_segment_type", "mpegts",
        "-hls_segment_filename", os.path.join(outdir, "seg%05d.ts"),
        playlist,
    ]
    proc = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    return proc, playlist


def find_cast(name):
    casts, browser = pychromecast.get_chromecasts(timeout=15)
    cc = next((c for c in casts if c.cast_info.friendly_name == name), None)
    if cc is None:
        names = ", ".join(repr(c.cast_info.friendly_name) for c in casts)
        browser.stop_discovery()
        raise SystemExit(f"Device {name!r} not found. Discovered: {names}")
    cc.wait(timeout=15)
    return cc, browser


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("device")
    ap.add_argument("--port", type=int, default=823)
    ap.add_argument("--fps",  type=int, default=8)
    args = ap.parse_args()

    if shutil.which("ffmpeg") is None:
        raise SystemExit("ffmpeg not found on PATH")

    outdir = tempfile.mkdtemp(prefix="cast_mock_")
    ip     = lan_ip()
    url    = f"http://{ip}:{args.port}/stream.m3u8"

    print(f"serving  {outdir}")
    print(f"stream   {url}")

    httpd = serve(outdir, args.port)
    proc, playlist = start_ffmpeg(outdir, args.fps)

    # Feed frames until the playlist exists, so the Chromecast never 404s.
    start = time.time()
    def pump():
        n = 0
        try:
            while proc.poll() is None:
                t   = n / args.fps
                img = render(t, datetime.now())
                proc.stdin.write(img.tobytes())
                n  += 1
                # keep roughly real-time
                target = start + t
                drift  = target - time.time()
                if drift > 0:
                    time.sleep(drift)
        except (BrokenPipeError, OSError):
            pass

    threading.Thread(target=pump, daemon=True).start()

    print("warming up encoder ...")
    for _ in range(60):
        if os.path.exists(playlist) and os.path.getsize(playlist) > 0:
            segs = [f for f in os.listdir(outdir) if f.endswith(".ts")]
            if len(segs) >= 2:
                break
        time.sleep(0.5)
    else:
        raise SystemExit("ffmpeg did not produce a playlist")

    print("connecting to device ...")
    cc, browser = find_cast(args.device)
    print(f"  {cc.cast_info.friendly_name} ({cc.cast_info.model_name}) "
          f"@ {cc.cast_info.host}")

    mc = cc.media_controller

    class Listener:
        def new_media_status(self, status):
            print(f"    media_status: player_state={status.player_state} "
                  f"idle_reason={status.idle_reason} "
                  f"content={status.content_id}", flush=True)

        def load_media_failed(self, item, error_code):
            print(f"    LOAD FAILED item={item} error_code={error_code}",
                  flush=True)

    mc.register_status_listener(Listener())

    cc.start_app(APP_DEFAULT_MEDIA)
    time.sleep(2)
    mc.play_media(url, content_type="application/x-mpegURL",
                  title="Mock Dashboard", stream_type="LIVE")
    mc.block_until_active(timeout=30)
    print(f"  player state: {mc.status.player_state}")
    print("\ncasting. Ctrl-C to stop.\n")

    try:
        while True:
            time.sleep(5)
            st = mc.status
            print(f"  [{datetime.now():%H:%M:%S}] state={st.player_state} "
                  f"app={cc.app_display_name}")
    except KeyboardInterrupt:
        print("\nstopping ...")
    finally:
        try:
            mc.stop()
            cc.quit_app()
        except Exception:
            pass
        browser.stop_discovery()
        try:
            proc.stdin.close()
        except Exception:
            pass
        proc.terminate()
        httpd.shutdown()
        shutil.rmtree(outdir, ignore_errors=True)


if __name__ == "__main__":
    main()
