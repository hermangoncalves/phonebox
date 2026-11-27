# Linux extended display — spike findings (pre-gate)

Date: 2026-11-26. Status: partial Task 0 evidence. Decision: **revise** (see §Decision).

This is the throwaway experiment from spec §7.0, not the full Task 0 gate: it proves the desktop half and plain
playback, and it leaves the pinned panel path, Wi-Fi, latency and pointer mapping to the implementation PRs, because
proving those requires the code Tasks 1–9 build. Experiment source lived in `/tmp/opencode/spike/` and is not kept.

## Inventory

| Item | Value |
|---|---|
| Session | Fedora 44, KDE Plasma 6.7.4, KWin 6.7.4, `XDG_SESSION_TYPE=wayland` |
| Displays | HDMI-A-1 1920×1080 at 0,0 · eDP-1 1920×1080 at 1920,0 |
| ScreenCast portal | version 5, `AvailableSourceTypes = 7` (MONITOR\|WINDOW\|VIRTUAL) |
| RemoteDesktop portal | version 2 |
| Python | `gi` with `Gio` and `Gst` (PyGObject); `Gst.version_string() = GStreamer 1.28.6` |
| GStreamer elements | `pipewiresrc`, `videoconvert`, `videorate`, `h264parse`, `mp4mux`, `appsink` present |
| H.264 encoders | `openh264enc` present; **`vah264enc` and `x264enc` absent** |
| ffmpeg | 8.1.2, no PipeWire input |
| Phone | SM-A156U1 (Galaxy A15), Android 16 / SDK 36 |
| WebView | `com.google.android.webview` 157.0.8103.50 |
| Link used | `adb reverse tcp:8099 tcp:8099`, plain HTTP, Chrome (not the app's WebView) |

## Reproduction

1. `python3 stream_to_phone.py <seconds>`: D-Bus `CreateSession` → `SelectSources{types=4 (VIRTUAL),
   multiple=false, cursor_mode=2}` → `Start{parent_window=""}` → `OpenPipeWireRemote` (fd via
   `Gio.DBusMessage.get_unix_fd_list()`).
2. Pipeline: `pipewiresrc fd=<fd> path=<node> ! queue max-size-buffers=2 leaky=downstream ! videoconvert !
   videorate ! video/x-raw,framerate=30/1 ! openh264enc bitrate=8000000 complexity=1 rate-control=bitrate
   gop-size=30 ! h264parse config-interval=-1 ! mp4mux streamable=true fragment-duration=100 !
   appsink emit-signals=true sync=false max-buffers=2 drop=true`.
3. Chunks are served as `Transfer-Encoding: chunked` on `http://127.0.0.1:8099/stream`; the page appends them to a
   `MediaSource` with `addSourceBuffer('video/mp4; codecs="avc1.42C028"')` and re-seeks to the live edge when the
   buffer runs more than 0.6 s ahead.

Each run needs one approval in the KDE dialog. The dialog is per session; there is no way to pre-authorise it in this
version (see Persistence).

## Pipeline / caps

- The portal created a **real virtual output**: `Virtual-virtual-xdp-kde-org.kde.konsole`, 1920×1080, placed at
  3840,0 and reported by `kscreen-doctor -o` while the session lived.
- Encoded output is **Constrained Baseline, level 4.0** (`avc1.42C028`); `ffprobe` on a file captured from the same
  pipeline confirmed `profile=Constrained Baseline, level=40, 1920x1080, 30/1`, boxes `ftyp, moov, moof, mdat`.
- Chrome on the phone accepted the codec string and decoded it. `openh264enc` rejects `rate-control=cbr`; the valid
  values are `quality`, `bitrate`, `buffer`, `off`. `gop-size=30` is required for a keyframe per second.
- The virtual output **disappears when the session ends** — `kscreen-doctor` listed it only while the helper ran. The
  spec's anti-goal ("no virtual monitor left behind") holds without extra cleanup code.

## USB results

Continuous synthetic 1080p30 over `adb reverse`, 65 s:

- Encoder: 50 chunks/s sustained (5 buffers per 100 ms fragment), stable for the whole run, ~1.3 MB total on a
  near-static pattern (the bitrate number is meaningless with that source).
- Phone: `chunks == appended` throughout, reaching 738 appended at t=45 s, no append error, buffer-ahead `lag`
  0.15 s → 0.53 s and not growing. Playback was visibly live (a moving test pattern advanced between screenshots).
- **CPU**: `openh264enc` at ~90 % of one core for 1080p30. This machine has no VAAPI encoder installed, so the
  software path is the only one available here.

A real virtual monitor with real desktop content also reached the phone: the captured frames showed the KDE panel,
virtual-desktop pager and wallpaper of the virtual output, and the same page played it.

**Damage-driven capture.** A virtual output that never changes produces almost no frames: the first run produced 40
chunks and then silence until the pointer was moved. The desktop must therefore not rely on a steady frame flow to
distinguish "idle desktop" from "dead stream"; a heartbeat and an explicit `stats`/state message are required, and the
last frame stays on screen when the desktop is idle.

## Wi-Fi results

Not tested. The spike used only the `adb reverse` USB route.

## Lifecycle

- The virtual output is created by `Start` and removed when the session (and its helper) goes away; observed once.
- `persist_mode=2` in `SelectSources` **crashed `xdg-desktop-portal-kde`** (response 2, DrKonqi dialog). Retried
  without `persist_mode` and the same run succeeded. Persistence is therefore unproven on this version and must be
  treated as optional.

## Persistence

Not proven. Every run showed the consent dialog again. The token flow (`restore_token` in `SelectSources`,
`restore_token` in the `Start` results) is implemented in the experiment but was never exercised successfully because
the only attempt with `persist_mode` crashed the portal.

## Congestion

Not exercised with a slow consumer. The design's bounded-queue + epoch/reset scheme is unproven; the observed
`lag` stayed under 0.6 s with the live-edge seek, which is the behaviour the design assumes but does not measure under
load.

## Input mapping

Not tested. No RemoteDesktop session was opened, so portal #2077 (absolute pointer on `VIRTUAL` streams) is still
open, and the `ydotool` fallback is still untested. Touch must stay behind the `control` option and out of the first
shipping PR until this is measured.

## Decision

**revise.**

The two questions that could have killed the feature are answered: KDE does hand out a real virtual monitor through
the portal with no privileges, and the phone's Chromium decodes the H.264 fMP4 stream continuously with bounded
buffering. What remains unproven — the pinned native path inside the app's WebView, Wi-Fi, glass-to-glass latency,
congestion recovery and pointer mapping — cannot be measured without the code that Tasks 1–9 build.

Concretely, the revision is:

1. Task 0 shrinks to what this document records. Its remaining items move into the PRs that make them measurable:
   pinned WebView playback into Task 10–12, Wi-Fi and latency into Task 14, pointer mapping stays the gate for Task 13.
2. The spec's "portal permission is remembered after the first approval" (G6) becomes a goal, not a promise:
   `persist_mode` crashes the portal on Plasma 6.7, so the first PR must ship without it and ask each session.
3. The design gains a heartbeat requirement, because KWin only sends frames when the screen changes.
4. `vah264enc` must be probed, not assumed: this machine has only `openh264enc`, which costs a full core at 1080p30.
   The widget must expose the 15 fps / lower-resolution fallback it already promises, and setup must say what to
   install.
