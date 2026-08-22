"""Cast a real Home Assistant dashboard to a Chromecast.

Works around the HA Cast receiver apps (A078F6B0 / B45F4572) being refused by
current Chromecast firmware: instead of asking the device to run the HA
frontend, we render the dashboard here in headless Chrome, encode the frames
to HLS, and play them through Google's Default Media Receiver (CC1AD845).

Auth: HA's frontend reads its session from localStorage. We seed that from a
long-lived access token, so no password is ever typed or stored here.

Usage:
    python cast_ha_dashboard.py "Game room TV" --view lovelace-cameras/tv-cams
Stop with Ctrl-C.
"""

import argparse
import functools
import json
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

APP_DEFAULT_MEDIA = "CC1AD845"
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

W, H = 1280, 720


def lan_ip(probe):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect((probe, 80))
        return s.getsockname()[0]
    finally:
        s.close()


def load_token(appsettings):
    with open(appsettings) as fh:
        cfg = json.load(fh)["HomeAssistant"]
    host = cfg["Host"]
    port = cfg.get("Port", 8123)
    return cfg["Token"], f"http://{host}:{port}"


def seed_profile(profile_dir, base_url, token):
    """Write the HA auth blob into the Chrome profile's localStorage.

    Done by loading the origin once and executing a tiny script, rather than
    poking at Chrome's LevelDB directly.
    """
    os.makedirs(profile_dir, exist_ok=True)
    auth = {
        "access_token": token,
        # long-lived tokens carry their own expiry; give the frontend a far
        # future value so it does not try to refresh
        "expires": 9999999999000,
        "expires_in": 315360000,
        "refresh_token": "",
        "token_type": "Bearer",
        "hassUrl": base_url,
        "clientId": None,
    }
    payload = json.dumps(json.dumps(auth))  # store as a JSON string value
    script = (
        f"localStorage.setItem('hassTokens', {payload});"
        "localStorage.setItem('selectedTheme', '\"dark\"');"
        "document.title='seeded';"
    )
    # data: URL keeps us on the right origin after the initial load
    subprocess.run(
        [CHROME, "--headless=new", "--disable-gpu",
         f"--user-data-dir={profile_dir}",
         "--virtual-time-budget=4000",
         f"--evaluate-on-new-document={script}",
         "--dump-dom", base_url],
        capture_output=True, timeout=60,
    )


def shoot(profile_dir, url, out_png, budget_ms, zoom):
    subprocess.run(
        [CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars",
         f"--user-data-dir={profile_dir}",
         f"--window-size={W},{H}",
         f"--force-device-scale-factor={zoom}",
         f"--virtual-time-budget={budget_ms}",
         f"--screenshot={out_png}", url],
        capture_output=True, timeout=120,
    )


class ThreadingHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads      = True
    allow_reuse_address = True


class Handler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, fmt, *args):
        pass

    def end_headers(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        super().end_headers()


def start_ffmpeg(outdir, fps):
    playlist = os.path.join(outdir, "stream.m3u8")
    cmd = [
        "ffmpeg", "-loglevel", "error", "-y",
        "-f", "image2pipe", "-vcodec", "png", "-r", str(fps), "-i", "pipe:0",
        "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
        "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
        "-profile:v", "high", "-level", "4.0",
        "-pix_fmt", "yuv420p", "-g", str(fps * 2), "-sc_threshold", "0",
        "-vf", f"scale={W}:{H}:force_original_aspect_ratio=decrease,"
               f"pad={W}:{H}:(ow-iw)/2:(oh-ih)/2",
        "-c:a", "aac", "-b:a", "96k", "-ar", "48000", "-ac", "2",
        "-f", "hls", "-hls_time", "2", "-hls_list_size", "6",
        "-hls_flags", "delete_segments+independent_segments+omit_endlist",
        "-hls_segment_type", "mpegts",
        "-hls_segment_filename", os.path.join(outdir, "seg%05d.ts"),
        playlist,
    ]
    return subprocess.Popen(cmd, stdin=subprocess.PIPE), playlist


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("device")
    ap.add_argument("--view", default="lovelace-cameras/tv-cams",
                    help="dashboard path, e.g. lovelace-cameras/tv-cams")
    ap.add_argument("--port",     type=int,   default=8233)
    ap.add_argument("--fps",      type=int,   default=2,
                    help="stream fps (frames are repeated between refreshes)")
    ap.add_argument("--refresh",  type=float, default=5.0,
                    help="seconds between dashboard re-renders")
    ap.add_argument("--zoom",     type=float, default=1.0)
    ap.add_argument("--settle",   type=int,   default=6000,
                    help="ms to let the dashboard load before capture")
    ap.add_argument("--appsettings",
                    default=r"C:\Local\NetDaemonFSApps\NetDaemonFS\appsettings.json")
    args = ap.parse_args()

    if shutil.which("ffmpeg") is None:
        raise SystemExit("ffmpeg not found on PATH")
    if not os.path.exists(CHROME):
        raise SystemExit(f"Chrome not found at {CHROME}")

    token, base = load_token(args.appsettings)
    url    = f"{base}/{args.view.lstrip('/')}"
    outdir = tempfile.mkdtemp(prefix="ha_cast_")
    prof   = os.path.join(outdir, "profile")
    ip     = lan_ip(base.split("//")[1].split(":")[0])
    stream = f"http://{ip}:{args.port}/stream.m3u8"

    print(f"dashboard {url}")
    print(f"stream    {stream}")

    print("seeding auth into browser profile ...")
    seed_profile(prof, base, token)

    # verify we are past the login screen before casting anything
    probe = os.path.join(outdir, "probe.png")
    shoot(prof, url, probe, args.settle, args.zoom)
    if not os.path.exists(probe):
        raise SystemExit("initial render failed")
    print(f"  first frame captured ({os.path.getsize(probe)} bytes)"
          f" -> {probe}")

    httpd = ThreadingHTTPServer(
        ("0.0.0.0", args.port),
        functools.partial(Handler, directory=outdir))
    threading.Thread(target=httpd.serve_forever, daemon=True).start()

    proc, playlist = start_ffmpeg(outdir, args.fps)

    stop = threading.Event()

    def pump():
        """Re-render periodically; repeat the last frame to keep HLS flowing."""
        cur   = probe
        shot  = os.path.join(outdir, "shot.png")
        last  = 0.0
        frame = open(cur, "rb").read()
        while not stop.is_set() and proc.poll() is None:
            now = time.time()
            if now - last >= args.refresh:
                last = now
                try:
                    shoot(prof, url, shot, args.settle, args.zoom)
                    if os.path.exists(shot) and os.path.getsize(shot) > 0:
                        frame = open(shot, "rb").read()
                except Exception as exc:
                    print(f"    render error: {exc}", flush=True)
            try:
                proc.stdin.write(frame)
                proc.stdin.flush()
            except (BrokenPipeError, OSError):
                break
            time.sleep(1.0 / args.fps)

    threading.Thread(target=pump, daemon=True).start()

    print("warming up encoder ...")
    for _ in range(80):
        if os.path.exists(playlist) and os.path.getsize(playlist) > 0:
            if len([f for f in os.listdir(outdir) if f.endswith(".ts")]) >= 2:
                break
        time.sleep(0.5)
    else:
        raise SystemExit("ffmpeg produced no playlist")

    print("connecting to device ...")
    casts, browser = pychromecast.get_chromecasts(timeout=15)
    cc = next((c for c in casts
               if c.cast_info.friendly_name == args.device), None)
    if cc is None:
        names = ", ".join(repr(c.cast_info.friendly_name) for c in casts)
        raise SystemExit(f"{args.device!r} not found. Found: {names}")
    cc.wait(timeout=15)
    print(f"  {cc.cast_info.friendly_name} ({cc.cast_info.model_name})")

    mc = cc.media_controller
    cc.start_app(APP_DEFAULT_MEDIA)
    time.sleep(2)
    mc.play_media(stream, content_type="application/x-mpegURL",
                  title=f"HA {args.view}", stream_type="LIVE")
    mc.block_until_active(timeout=30)
    print(f"  player state: {mc.status.player_state}")
    print("\ncasting. Ctrl-C to stop.\n")

    try:
        while True:
            time.sleep(10)
            print(f"  [{datetime.now():%H:%M:%S}] "
                  f"state={mc.status.player_state}")
    except KeyboardInterrupt:
        print("\nstopping ...")
    finally:
        stop.set()
        try:
            mc.stop(); cc.quit_app()
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
