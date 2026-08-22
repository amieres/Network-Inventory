# Handover — Camera DVR / cast pipeline

Written 2026-08-22. Supersedes the "cast a dashboard to Chromecast" line of work.
Read `HANDOVER-kasa-cameras.md` too if you touch the Kasa cameras.

---

## 1. Why this pivot

The original goal was to restore `cast.show_lovelace_view` for a camera view. That
turned out to be impossible from the HA side (see §2), so the work became "get a
live camera grid onto the Chromecast", which mostly succeeded but hit a latency
wall (see §5).

**New goal:** a DVR. Persist frames/segments to disk so the app can

- serve a **live** view, and
- **seek back in time** to replay events.

This is a substantial rewrite, not a patch. The current app composes frames in
memory and forwards them; nothing is retained.

---

## 2. Settled facts — do not re-investigate

### HA Cast receiver apps are dead on this hardware

Probed directly from the PC with `pychromecast`, HA entirely out of the loop, on
**two device generations** (Google TV Streamer + an older Chromecast):

| App | ID | Result |
|---|---|---|
| Google Default Media Receiver | `CC1AD845` | launches, plays |
| HA Lovelace receiver | `A078F6B0` | `Failed to execute start app` |
| HA Media receiver | `B45F4572` | `Failed to execute start app` |

`cast.show_lovelace_view` therefore only ever flickers the TV. Matches upstream
[home-assistant/core#177170](https://github.com/home-assistant/core/issues/177170)
(open, assigned to `emontnemery`), though our failure is at *app launch*, earlier
than that report's namespace error. **Nothing in the HA config is wrong.** The
following were each checked and cleared: internal/external URL, TLS cert, DNS,
NAT hairpin, CORS, `trusted_proxies`, the Cast integration's system user and
token, entity duplication, and device firmware.

Repro script: `cast_mock/` (see §6).

### Chromecast media behaviour

- **MJPEG** (`multipart/x-mixed-replace`): loads but stays `paused` **behind**
  the running app — the TV never switches to it.
- **Still JPEG**: *does* take the foreground, but is one frame and freezes.
  Re-casting on a timer to animate it flickers unbearably (each re-cast is a
  full media load).
- **HLS via go2rtc `stream.m3u8`**: go2rtc mints a **new session id on every
  master-playlist request**, so the receiver's second fetch lands on a different
  session and the stream dies.
- **Fragmented MP4** (`stream.mp4`): stable URL, **this is the one that plays**.
- `play_media` alone loads the receiver *behind* whatever app is running. Call
  `media_player.turn_off` first, wait ~6s, then `play_media` — that forces the
  takeover. Verified: foreground goes `com.pbs.video` → `com.google.android.apps.mediashell`.
- For a **live** stream `media_position` does not advance. `state: playing` +
  `go2rtc consumers: 1` are the real health signals, not position.

### Camera facts (measured)

RTSP is on port **8554**, not 554 (554 is actively refused on these units).

| Camera | RTSP | ch101 | ch102 |
|---|---|---|---|
| Driveway | `192.168.5.9:8554/Streaming/Channels/<ch>` | h264 2304x1296 | h264 640x360 |
| Front door | `192.168.5.17:8554/Streaming/Channels/<ch>` | **hevc** 2592x1944 | h264 640x480 |

**Front door ch101 is HEVC — Chromecast cannot decode it.** Always use ch102.
Credentials are in `.storage/core.config_entries` (onvif entries, `admin` +
13-char password) and already inlined in `go2rtc.yaml`.

`/api/camera_proxy/<entity>` snapshot latency (HA transcodes a frame from RTSP
per request — these are floors):

| Camera | Latency |
|---|---|
| kasa_garage / kasa_attic | ~2.2s |
| front_door_frontdoorcam | ~3.5s |
| driveway_cam_mainstream | ~4.0s |
| `*_substream` | ~5.4s (slower despite being smaller) |
| amcrest substream | ~8.3s |

Substreams are **slower** than mainstreams — the cost is transcode setup, not
image size. Don't reach for them to speed things up. All ONVIF cameras report
`supported_features = 2` (STREAM only, no native still endpoint).

---

## 3. Current state

### Working

- `driveway_sub`, `frontdoor_sub`, `kasa_garage`, `kasa_attic` in go2rtc — all
  serve RTSP fine. Source lag measured at **~3s**, which is acceptable.
- NetDaemon app `CastDashboard.CameraCast` — builds, deploys, runs.
  `/cast/cameras.jpg` returns 200 with a correct 2x2 grid.
- Services `netdaemon.cast_cameras` / `netdaemon.cast_cameras_stop`.

### Broken right now

- **`cameras_grid` in go2rtc is failing** — `stream.mp4?src=cameras_grid`
  returns `000`; the `exec:` ffmpeg dies before producing output. The TV is not
  currently showing anything from this pipeline. This is where work stopped.

### Files

| Path | Role |
|---|---|
| `NetDaemonFS/src/CastDashboard.fs` | the app: snapshot loops, SkiaSharp grid, MJPEG/JPEG endpoints, cast services |
| `NetDaemonFS/Yaml/CastDashboard.yaml` | app config (cameras, mediaPlayer, streamUrl, fps) |
| `//192.168.5.70/config/go2rtc.yaml` | camera sources + `cameras_grid` ffmpeg filter |
| `backups/go2rtc.yaml.bak-*` | timestamped backups; `-20260822-*` are from this session |
| `cast_mock/` | standalone PC-side tools + the receiver-refusal diagnostic |

---

## 4. Traps that cost real time

**`publish.ps1` used to destroy the deployment.** Line 22 was
`Remove-Item -Recurse -Force <target>\*`, which deleted `appsettings.json` (it
lives **only** on the share — `CopyToOutputDirectory=Never`) and every
`runtimes/<rid>/native` directory. SMB cannot create those subdirectories
mid-copy, so publishes then failed with a cascade of MSB3021/MSB3027 errors, and
NetDaemon refused to start with:

```
FileNotFoundException: The configuration file 'appsettings.json' was not found
```

**Already fixed** — it now deletes files but preserves directories, and restores
`appsettings.json` afterwards. Publishes are clean (0 errors). Don't reintroduce
a recursive wipe.

**Supervisor token override.** The add-on gets `HomeAssistant__Token` injected
from the Supervisor, and it outranks `appsettings.json` in the config chain. That
token gets a **401 from `/api/camera_proxy`**. The app reads the long-lived token
straight out of the file to work around this — keep that if you keep snapshots.

**SkiaSharp needs the NoDependencies build.** The add-on container has no
`libfontconfig`, so the normal `SkiaSharp.NativeAssets.Linux` fails with
`DllNotFoundException: libfontconfig.so.1`. Use
`SkiaSharp.NativeAssets.Linux.NoDependencies`. Consequence: **no system font**,
so text overlays silently don't render (the code guards for a null typeface).
Camera-burned timestamps are the liveness indicator instead.

**The NetDaemon→go2rtc MJPEG hop is unreliable.** Kestrel dropped the streaming
response unpredictably — three identical requests ran 9.9s, 2s, and 35s. go2rtc
logged `Connection reset by peer` / `EOF` and playback stalled. The endpoint
itself delivers frames correctly (measured 9.2fps at a configured 10). **Avoid
putting this HTTP hop in the video path.**

**ffmpeg flags that break go2rtc `exec:`** — all tried, all failed:
- `-avioflags direct` + `-fflags nobuffer` → ffmpeg races ahead of real time,
  buffers minutes of video, and libx264 warns `MB rate (165600000) > level limit`.
- `-vsync drop`, `-use_wallclock_as_timestamps 1` → strip timestamps the RTSP
  muxer needs; go2rtc kills the process with `[exec] timeout`.
- What *did* test clean locally: `fps=N` filter on **each** input plus `-r N` on
  the output, pinning it to real time. Verified `rc=0`, no level warnings, with
  2 inputs. **Not yet verified with 4 inputs** — that's exactly where it stopped.

---

## 5. The unsolved problem

**The grid stream ran ~4–5 minutes behind while the source RTSP was only ~3s
behind.** Proven by reading the cameras' burned-in timestamps: a frame captured
at 03:04:37 showed 03:00:23.

So the lag is introduced entirely by the `cameras_grid` ffmpeg process, not by
the cameras, not by go2rtc's RTSP passthrough, and not by the Chromecast. The
process runs continuously from go2rtc start and accumulates buffer.

Framerate on the TV was **good** — the picture looked fine, it was just minutes
stale. Fixing this by tuning ffmpeg flags is what the DVR pivot replaces.

---

## 6. `cast_mock/` — standalone PC-side tools

Independent of HA; useful for testing without touching the add-on.

| File | Purpose |
|---|---|
| `make_dashboard.py` | renders a mock dashboard (clock, cards, animated bars) with Pillow |
| `cast_mock.py` | pipes frames → ffmpeg HLS → serves → casts via `CC1AD845`. **Works.** |
| `cast_ha_dashboard.py` | headless-Chrome capture of a real Lovelace view. **Blocked** — see below |
| `go2rtc_grid.jpg`, `grid_preview.jpg` | reference captures |
| `_rtsp.json` | resolved RTSP URLs (contains credentials) |

Run: `python -u cast_mock.py "Game room TV" --port 8232 --fps 8`

**`cast_ha_dashboard.py` is blocked on auth and should probably be abandoned.** A
long-lived token **cannot** seed a frontend session — HA redirects to
`/auth/authorize` — and it **cannot** be exchanged at `/auth/token`
(`invalid_grant`). The frontend needs a real OAuth session with a working refresh
token. The only clean routes are a one-time manual headed login persisted in a
Chrome profile, or a `trusted_networks` auth provider. I would not automate a
password login.

---

## 7. Suggested direction for the DVR

Not prescriptive — the design is open.

**Recording.** Let go2rtc/ffmpeg write **HLS or fMP4 segments** to disk per
camera (e.g. 2–4s segments), rather than compositing a grid in real time.
Segmented files give both live playback (tail of the playlist) and seeking (any
older segment) from the same artifacts, and sidestep the whole real-time
compositing latency problem in §5. Consider recording each camera separately and
compositing only on demand.

**Retention.** Needs a purge policy and a disk-space budget. `/config` is on the
HA host — check free space before choosing segment length and retention. A
sidecar index (SQLite) mapping timestamp → segment file would make seeking cheap;
the repo already uses `Microsoft.Data.Sqlite` (see `Inventory/Database.fs`).

**Serving.** Kestrel already runs in the NetDaemon app on **port 10000**
(`Inventory/WebHost.fs`, Falco routes in `Inventory/Api.fs`). Static segment
serving is a good fit. But note §4: don't put a *live-generated* MJPEG stream in
the video path — serving *finished files* is a different and much safer thing.

**Casting.** Keep what's proven: fragmented MP4 or a static HLS playlist,
`turn_off` → wait → `play_media`, `CC1AD845`.

**Open questions for the user:**
- Retention window and acceptable disk usage?
- Per-camera recording, or only the composed grid?
- Does the DVR UI live in the existing wwwroot app, a Lovelace card, or the TV?
- Is the ~3s source latency acceptable for "live", or is lower needed?

### Using the local LLM

Available: Unsloth Studio at `http://100.117.173.25:8888` (needs a Bearer key).
Loaded models include `Qwen3.8-27B-Q5`, `ornith-35B`, `Muse-Glimmer-30B`,
`gpt-oss-20b`. Tools: `mcp__llamacpp__local_llm`, `local_llm_batch`,
`local_llm_start` + `local_llm_check` for background jobs, `local_llm_plan`.

Good candidates to offload: drafting the segment-index schema, generating
boilerplate Falco route handlers, ffmpeg command permutations for segmenting,
retention/purge logic. Keep anything that needs live measurement against the real
cameras in the main session — this session's repeated lesson is that
**assumptions about ffmpeg/Chromecast behaviour must be measured, not reasoned
about**.

---

## 8. Deploy

```bash
cd NetDaemonFS && powershell -ExecutionPolicy Bypass -File publish.ps1
```

The script's stop/start uses the HA PowerShell module, which usually fails DNS.
Restart the add-on directly instead:

```bash
curl -s -X POST "http://192.168.5.70:8123/api/services/hassio/addon_restart" \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"addon": "c6a2317c_netdaemon5"}'
```

Token: `NetDaemonFS/appsettings.json` → `HomeAssistant.Token`.
go2rtc add-on slug: `a889bffc_go2rtc`. Allow **~80s** after restart before the
app's endpoints respond.

**Restarting NetDaemon takes down the other apps too** (EG4 battery, RebootRaspi,
Inventory) — confirm with the user before deploying.

---

## 9. Note on process

Several fixes in this session were deployed before being verified end to end on
the TV, which cost the user real time and produced an unpleasant flickering
state. The pattern that worked was: measure first (timestamps, `ffprobe`, HTTP
status, go2rtc `consumers`), then change one thing. Please keep to that.
