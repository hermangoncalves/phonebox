# PhoneBox: the phone as a second monitor on Linux (Screen widget)

Status: revised draft, 2026-11-26. Target: KDE Plasma on Wayland. This document authorizes no product implementation;
implementation follows approval and the feasibility gate in §7. Changes in this revision address transport,
portal selection, input mapping, session ownership, congestion recovery and the unsupported decoder fallback.

## 1. Intent and why

Let the paired phone act as a real second monitor: the Linux desktop gains a display, windows can be dragged onto
it, and the existing PhoneBox panel shows its contents. Optional touch control acts on the displayed desktop.
The feature uses the existing app, pairing and USB / Wi-Fi / Tailscale routes.

KDE's ScreenCast portal advertises a `VIRTUAL` source, so the proposed path needs capture, encoding and transport
rather than a new display driver. Portal availability alone does not prove that the complete feature works.
The desktop/Chrome spike is recorded in [the findings](2026-11-26-linux-extended-display-spike-findings.md).
Authenticated playback inside the actual Android WebView remains a gate for the widget milestone, not a prerequisite
for building the transport that makes that test possible.

## 2. Goals, non-goals and boundaries

- **G1 Extend.** Create one virtual monitor for a phone. Target 1920×1080 on the tested KDE version.
- **G2 Mirror.** Show one existing monitor chosen through the computer's portal dialog.
- **G3 Touch.** Optional tap, drag, two-finger scroll and long-press right click. `control` defaults to false.
  Input is available only when permission is granted and an accurate mapping has been demonstrated for that source.
  Video can ship without touch; unsupported control has an explicit state and never silently becomes relative input.
- **G4 Reuse.** Existing pairing, routes, panel and page/widget model. A native Android transport bridge is in scope;
  decoding and rendering remain in WebView MSE.
- **G5 Quality.** 1080p30, 8 Mbit/s, hardware H.264 where available. Target median glass-to-glass latency ≤300 ms
  under the measured normal USB and Wi-Fi conditions. Software H.264 and 15 fps are documented lower-quality modes.
- **G6 Lifecycle.** Capture is requested only while visible. Explicit close stops immediately; an unresponsive
  viewer is stopped within 10 seconds. V1 asks for portal consent on each new session; persistence is deferred because
  the spike crashed the installed KDE portal when requesting it.
- **G7 Portability.** Other platforms and unsupported sessions show a reason; saved layouts remain portable.

V1 excludes Windows, macOS, GNOME, X11 and wlroots support; more than one active screen per phone; more than two
active sessions per agent; audio, keyboard, clipboard and file transfer; HDR; output above 1080p or 30 fps; a native
video decoder; MJPEG; and automatic output placement. GNOME is outside the support matrix, not inherently missing
the portal.

Anti-goals: a new kernel module, root requirement, evdi, a separate phone app/player, streaming before the user adds
a Screen widget, input without explicit per-widget consent, and commands, paths or arbitrary URLs supplied by the
phone. Existing ydotool installation does not establish a working absolute-pointer fallback.

## 3. Evidence and assumptions

The [spike findings](2026-11-26-linux-extended-display-spike-findings.md), committed in `97ff33d`, record this
target-machine inventory and successful virtual-display/software-encode/plain-Chrome playback on 2026-11-26.
They do not establish pinned app playback, Wi-Fi, glass-to-glass latency, congestion recovery or input support:

- Fedora 44, KDE Plasma / KWin 6.7.4, Wayland, two 1920×1080 monitors.
- ScreenCast portal version 5, `AvailableSourceTypes = 7`; RemoteDesktop version 2.
- The draft reports fixed 1920×1080 virtual outputs on this KDE version. V1 does not depend on later resize APIs.
- ffmpeg 8.1.2 without PipeWire input; GStreamer 1.28.6 with `pipewiresrc` and `appsink`. No VAAPI encoder is
  installed, but the software `openh264enc` is; `x264enc` is not. Encoder availability is re-probed at runtime.
- ydotool present; xdotool absent. Neither is required by this design for screen input.
- Portal [issue #2077](https://github.com/flatpak/xdg-desktop-portal/issues/2077) reports absolute input failure for
  virtual streams. Test the installed version rather than assuming availability or a fix.

Repository facts that constrain the design:

- Android loads the panel from its asset origin. `MainActivity.kt` proxies `/agent/` HTTP requests through
  `AgentLink.kt`, which owns TLS pinning, credentials and route selection. The native trackpad socket is precedent
  for authenticated WebSockets. A direct JavaScript WebSocket does not reuse this proxy or its authentication.
- The panel supports Chromium 83+, with Android 5 WebView capped at 95. Actual H.264 profile/level support, MSE,
  autoplay and bridge throughput must be tested on the target phone.
- CONTRIBUTING requires a widget registry entry, JS, icon, README count, demo mock and docs. Input must be checked
  against enabled widgets with `control` off by default. A new LAN path needs justification; PRs should stay near
  800 changed lines outside tests. Open the required feature issue before implementation.

Authoritative API contracts:

- [ScreenCast](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html): source
  selection, cursor modes, PipeWire access and stream metadata. Position and size are optional; position is only
  defined for monitor streams, and compositor coordinates can differ from video pixels.
- [RemoteDesktop](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html):
  device selection and persistence for combined sessions. Restore tokens are single-use and replaced on Start.
- [GStreamer mp4mux](https://gstreamer.freedesktop.org/documentation/isomp4/mp4mux.html): fragmented MP4 output.
  Encoder properties and negotiated caps must be verified against the installed plugins.

## 4. Approach

**Chosen: portal capture + Python helper + agent relay + native Android transport + WebView MSE.** The helper owns
D-Bus and GStreamer. The agent owns authorization, lifecycle and relay limits. Android owns pinned transport and
bridges bounded chunks to the panel. The widget owns MSE and touch gestures.

**Mirror only** uses the same pipeline but does not deliver a second monitor; it is an incremental milestone.
**evdi / Xorg dummy** adds privileged installation and a different support burden; excluded.
**RDP through a separate client** is an external alternative to investigate if the spike fails, not a promised
fallback within PhoneBox. A native decoder or MJPEG would require a revised spec and separate feasibility work.

## 5. Design

### 5.1 User experience and options

New **Screen** widget, category Computer, icon 🖥️. Adding the widget is the opt-in to capture. No default layout
contains it. The first start explains that sharing approval happens on the computer.

| key | label | type | default | contract |
|---|---|---|---|---|
| `mode` | What to show | select | `extend` | `extend` or `mirror` |
| `fps` | Frames a second | select | `30` | 10 / 15 / 30 |
| `quality` | Quality | select | `medium` | low / medium / high = 4 / 8 / 16 Mbit/s |
| `control` | Allow controlling the pointer from the phone | bool | `false` | requires granted input and supported mapping |
| `cursor` | Cursor | select | `embedded` | `embedded` or `hidden`, checked against advertised modes |

Video always uses `contain`; no crop option. Mirror capture is scaled down to fit within 1920×1080 while preserving
aspect ratio. The portal selects the physical monitor; “Choose monitor again” closes the current mirror session
and restarts through the dialog. V1 stores no selection token. No monitor identifiers or output names are accepted from the phone.

States: starting, needs approval, no helper, no encoder, live, paused, busy, unsupported, denied and lost. Reasons
are fixed codes localized by the panel. A lock indicates control is off; “Pointer control unavailable” explains a
failed permission/mapping independently of live video. The widget offers Stop and Retry; Stop closes the session
until a hidden-to-visible transition or Retry is pressed. A stopped capture removes its virtual monitor.

### 5.2 Data flow and Android boundary

```
WebView screen.js (MSE) ⇄ keyed Android screen bridge
                                  ⇅ bounded chunks / input / playback acknowledgements
                         AgentLink pinned WSS
                                  ⇅ /api/screen?widget=<pageId>/<widgetId>
                         agent ScreenFeeds
                                  ⇅ private Unix socket
                         Python Gio + Gst helper
                                  ⇅ portal + PipeWire
                         KWin virtual output / chosen monitor
```

Extend the existing keyed, main-frame Android bridge with open, close, input and acknowledgement operations.
JavaScript supplies the widget key and a local viewer handle; native code constructs the endpoint, selects the
active paired computer and supplies Authorization and device headers using existing AgentLink rules. It never
accepts a host, port, URL or credential from JavaScript. TLS pinning applies on both USB and Wi-Fi. Credentials
remain native. Switching computers or routes closes the old connection before opening the new one.

Native code delivers text events and base64 binary chunks through main-frame callbacks. Each callback carries
viewer handle, epoch and sequence; a closed/replaced viewer ignores stale callbacks. Base64 is a transport detail,
not a native player. Limit a chunk to 256 KiB, split larger mux output, and allow at most 512 KiB of unacknowledged
binary data across the native-to-JS bridge. The native-transport/widget milestones must measure copying overhead and throughput at 8 Mbit/s.
If this fails the target, revise this boundary before that surface ships; do not expose pairing secrets to WebView.

The web demo supplies a fake bridge. Without a native screen bridge, the widget displays unsupported. A direct
browser WebSocket path is outside v1.

### 5.3 Helper and portal handshake

Embed `agent/PhoneBox.Agent/Linux/phonebox-screencast.py` as a resource and run it with system `python3` using
`ProcessStartInfo.ArgumentList`. Gio and Gst come through PyGObject. Probe actual imports, `pipewiresrc`, the muxer,
and working encoders; package installation alone is insufficient. `setup.sh` offers distro-specific dependencies
with its existing confirmation flow. Prefer `vah264enc`; fall back to the installed `openh264enc`, then `x264enc`,
both with low-latency settings.

Use a private 0700 runtime directory and 0600 Unix socket; remove both on teardown. Frames are
`[kind: u8][length: u32 big-endian][payload]`. JSON is kind 1 (≤64 KiB); video is kind 2 (≤256 KiB). Reject unknown
kinds, oversized or truncated frames. The helper has no LAN listener.

- Commands: `start{mode,fps,bitrate,cursor,control}`, `resetMedia{epoch}`, `stop`,
  `pointer{x,y}`, `button{n,down}`, `scroll{dx,dy}`, `releaseAll`.
- Events: `ready{width,height,codec,inputAvailable}`, `media{epoch}`, `error{code}`, `stats{fps,bitrate}`.
  V1 neither requests, stores nor forwards restore tokens; unexpected token fields are discarded.
- Video payload begins with epoch (u32) and sequence (u32), followed by an ordered slice of fragmented MP4.
  A media event precedes each epoch. Each epoch starts with the MP4 initialization segment, followed by media
  beginning at a decodable keyframe. Chunk boundaries need not match MP4 box boundaries.

Portal calls, with each asynchronous Request response awaited before the next dependent call:

1. Check session, source and cursor capabilities. With `control=false`, create a ScreenCast session.
2. With `control=true` and the input gate passed, create a RemoteDesktop session and call
   `RemoteDesktop.SelectDevices(types=POINTER)`. Do not request keyboard or touchscreen devices.
3. Call `ScreenCast.SelectSources(types=VIRTUAL or MONITOR, multiple=false, cursor_mode=...)` with that session.
   Omit `persist_mode` and `restore_token` on both interfaces in v1; capability/version advertisement does not
   override the observed portal crash. Do not probe persistence automatically against a running desktop.
4. Call Start on the owning interface (ScreenCast for capture-only, RemoteDesktop for combined). Validate returned
   source and granted devices. Each new session requires consent; a media reset within a live session does not.
5. Call `ScreenCast.OpenPipeWireRemote`, retain the returned FD and select the returned stream node. Resolve the
   negotiated video dimensions from PipeWire/GStreamer caps, not assumed portal metadata.
6. Observe Session.Closed. On stop, release held input, stop the pipeline, close the portal session and PipeWire FD,
   then exit. If graceful shutdown exceeds two seconds, the agent kills the helper process tree.

A video-only session never requests pointer permission. If a combined session grants capture without pointer,
continue video with input unavailable. Denial does not trigger automatic attempts to obtain broader permission.

Capture pipeline shape (illustrative, not a literal gst-launch command):

```
pipewiresrc (portal FD + node) → convert / scale / rate limit
  → negotiated raw caps → low-latency H.264 encoder → h264parse
  → fragmented MP4 muxer → appsink → framed socket writer
```

Negotiate an H.264 profile/level supported by the target WebView and encoder. Disable frame reordering, target a
keyframe at least every second, and target 100 ms fragments. Report the actual RFC 6381 codec string in ready.
Keep raw-frame queues bounded and drop old raw frames before encoding. Do not discard arbitrary encoded chunks.
Encoder fallback occurs within the same portal session and creates a new media epoch. For the observed
`openh264enc`, use `rate-control=bitrate` and `gop-size=<fps>` rather than VAAPI-specific property names; the spike
produced Constrained Baseline level 4.0 (`avc1.42C028`), but report negotiated caps rather than hardcoding the codec.
Keep post-encode appsink/socket delivery lossless within the bounded recovery policy. The throwaway spike’s
`appsink drop=true` and live-edge seeking are not proof of safe product congestion handling.

KWin capture is damage-driven. Emit helper stats every two seconds on a timer independent of frame callbacks.
An idle desktop may legitimately emit no video for longer than the heartbeat deadline: retain the last image and
keep the session live while viewer heartbeats and helper health messages arrive. Apply send timeouts only to
pending writes, never to the interval between video chunks. No frame-arrival watchdog may restart an idle desktop.

### 5.4 Ownership, consent and lifecycle

`ScreenFeeds` owns at most one active viewer/helper per device, and at most two globally. Its identity includes the
device, widget key and selection scope `(mode, control)`. Another widget from that device receives busy while one
owns the screen; page transitions close the old viewer first and Retry can acquire the released slot. Reconnecting
the same widget supersedes its previous connection atomically; stale close callbacks cannot stop the replacement.

V1 has no restore-token store and does not request persistence. Serialize session starts per device, ask for consent
on each new session, and never automatically reopen a portal dialog after helper/portal failure. “Choose monitor
again” is an enumerated action scoped to the active mirror viewer; it closes and reopens the session with consent.

Future persistence requires a separate reviewed change with evidence that the portal no longer crashes. That change
must scope tokens by `(device, mode, control)`, consume them durably before use and save replacements atomically;
combined-session persistence belongs in SelectDevices, capture-only persistence in SelectSources.
Close immediately on hide, Stop, destruction, route/computer change or unexpected WebSocket close. The sole
exception is an explicitly coordinated media-recovery reconnect in §5.5. While visible, send a
heartbeat every two seconds; ten seconds without one tears down the helper and releases the slot, including a
pending approval dialog. Do not apply the camera's last-HTTP-request timer to this persistent connection. Observe
configuration saves: widget removal, disabled page or mode/control changes invalidate the active session immediately.
Track helper stats separately: six seconds without helper health marks capture lost and closes it, even when viewer
heartbeats continue. Healthy stats with no video means idle, not lost; health emission also runs during approval.

Agent restart ends all sessions; no live session survives it. A visible widget reports lost and offers Retry, which
starts a fresh session and consent dialog. Transport recovery within an already-live portal session remains automatic.
Helper/portal loss, denial, unsupported decode, missing dependencies and unsupported capabilities wait for Retry.
Bounded transport retries (1, 2, 4, 8 and then 30 seconds while visible) must never create a new portal session or
repeated approval dialogs. If a live session cannot be retained, stop and require Retry.
Session cleanup also runs on agent shutdown and failed startup; a helper must exit if its agent socket disappears.

### 5.5 Relay, MSE and congestion recovery

`/api/screen` accepts authenticated WebSocket upgrades and the recovery POST described below, only after RequestGate
and enabled-widget/device validation. Add it
to the existing phone route allow-list; explain in the PR that it exposes selected desktop pixels and gated input.
The server derives capture settings from the widget. Client text messages contain only fixed operation names,
viewer/epoch/sequence identifiers and bounded numeric values; no capture paths, URLs or arbitrary pipeline options.
Windows/macOS expose no capture endpoint. Enforce message lengths and input rate limits before helper forwarding.

Server-to-phone: ready, media, state and video chunks. Client-to-server: heartbeat, input, media acknowledgement,
reset request and choose-monitor action. Strip private helper events. Reject malformed/non-finite coordinates;
clamp finite normalized coordinates to [0,1] and bound button/scroll values. Recheck `control` and device/widget
ownership for every input message; a successful upgrade does not grant lasting input authorization.

Screen.js creates a muted inline video and MSE SourceBuffer from ready.codec. Check `isTypeSupported`, handle
SourceBuffer errors and autoplay rejection, and serialize append/remove calls. Decode errors close capture and show
unsupported with Retry. There is no MJPEG negotiation or fallback endpoint in v1.

Bound every queue: agent unsent video ≤1 MiB; native bridge ≤512 KiB unacknowledged; JS pending appends ≤512 KiB.
Trim played MSE data older than two seconds and retain at most one second ahead of playback. JS reports epoch,
last appended sequence and playback lag at least twice a second. An append acknowledgement releases bridge credit;
it is not proof that the frame has been displayed.

Any queue overflow, send stalled for two seconds, or reported playback lag >500 ms triggers recovery: stop sending
the old epoch, clear queued chunks, reset MSE, and request a fresh encoder/muxer epoch in the existing portal
session. Coordinate recovery through the existing authenticated HTTP proxy: a bounded request naming the widget
and current viewer handle marks the session for recovery before native code closes and replaces its WebSocket.
The same `/api/screen` allow-list entry covers this POST; reject arbitrary fields and stale handles. Permit retention
only for that device/widget's replacement, with the ten-second heartbeat deadline still enforced. If coordination
fails, close the helper and reconnect normally. Replacement transport discards bytes already queued by the old
connection. Resuming mid-fragment
or dropping H.264 reference frames is forbidden. Three resets within 30 seconds stop capture as “connection too
slow”; Retry starts again. Blocking writes alone do not establish a latency bound.

### 5.6 Touch and coordinate safety

Map touch coordinates against the actual contained video rectangle, excluding letterbox bars. Ignore touches in
bars. Account for source aspect ratio and negotiated dimensions; a rotated phone changes the display rectangle,
not the desktop's coordinate space. Portal absolute calls use the selected stream ID and its documented logical
coordinate space. Video dimensions must not be substituted for logical dimensions on scaled outputs.

Input requires both granted pointer permission and a validated mapping for that source. The input milestone must establish
how logical extents are obtained on the installed KDE, including virtual streams with missing position/size metadata.
If extents cannot be established or #2077 prevents absolute input, show control unavailable and send no input.
Do not guess virtual-output desktop coordinates from optional portal metadata. ydotool absolute fallback is deferred;
adding it requires an independently proven output mapping and a revision to this contract.

Track pressed buttons per viewer. Release them on touchcancel, hide, Stop, disconnect, permission loss, config
change, media reset and helper failure. Serialize gestures so a long press cannot also generate a left click and a
second finger cancels pending tap/long-press handling before scroll. Unit-test these transitions.

### 5.7 Failure states

| Condition | Result |
|---|---|
| Unsupported OS/session/source capability | unsupported, reason; no helper |
| Another screen owns this phone or global limit reached | busy; Retry |
| Portal approval pending | needs approval; hide/Stop cancels it |
| User denied or cancelled | denied; explicit Retry |
| New session or reselection | needs approval each time; no stored token in v1 |
| Missing Python/Gio/Gst or usable encoder | no helper / no encoder; documented install instructions |
| Decoder/profile/MSE failure | unsupported decoder; close capture; Retry |
| Input unavailable | live video with control unavailable |
| Helper/portal/output lost | lost; explicit Retry starts a new consent dialog |
| Repeated congestion | connection too slow; stopped until Retry |

### 5.8 Packaging, privacy and documentation

Embed the helper in the Linux agent; no separate helper binary distribution. Probe packages by capability and
verify names for each supported distro. Missing optional dependencies must not prevent the normal agent starting.
Android transport changes require the normal lint/build checks and API 21 compatibility. No new Android permission
is expected. Keep the widget on every platform for layout portability.

V1 adds no token storage. Discard unexpected portal tokens and exclude them from `ToPublic()`, phone events, logs
and demo fixtures. A later persistence change must use owner-only storage and remove records with the device. Document that mirror
mode sends
selected desktop content to the paired phone. No new external service is introduced; check existing privacy wording
for accuracy about desktop streaming even though no new outbound host row is required.

Update GUIDE, SETUP, widget registry, icons, demo mock and README count using the actual current count. Leave
CHANGELOG and version numbers to the maintainer. Explain portal consent, Stop removing the virtual monitor,
re-selection, unsupported touch and decoder limits.

### 5.9 Verification

- C#: device/widget gating; config revocation during an open socket; one-session ownership; two-session cap;
  absence of persistence options/token storage; heartbeat expiry; idle-desktop health; frame limits; transport retry; secret filtering;
  queue overflow and epoch recovery; releaseAll on every teardown path.
- Fake helper: protocol events, fragmented MP4 fixture, delayed/truncated frames, crashes and input echoes. CI must
  not need a real portal. A colour-pattern message alone does not test MSE decoding.
- UI: demo bridge, state transitions, MSE append/reset ordering, stale callback rejection, visibility and contained
  touch mapping, cancellation/release behavior, input absent with control off or unavailable.
- Android: pinned USB/Wi-Fi connection, invalid certificate/key rejection, bridge-key enforcement, stale viewer
  callbacks, computer/route switching, bounded buffering, background/screen-off cleanup; lint and debug build.
- Hardware: extend/mirror, monitor reselection, two phones, two screen widgets on one phone, scaled and rotated
  sources, negative desktop placement, portal denial/revocation/restart, app kill, cable loss and agent restart.
- Performance: ten minutes each over USB and Wi-Fi on the actual phone; record negotiated codec, rate/bitrate,
  CPU/GPU, memory, thermal behavior and latency median/p95 with measurement conditions. Add ten seconds of link
  throttling below the stream bitrate, then restore it; playback must recover to ≤500 ms lag within five seconds
  or enter the explicit stopped state. Memory must respect queue limits throughout.

## 6. Remaining risks and decisions

- **Portal input:** video is the baseline. Absolute control ships only for mappings proven in the input milestone; no guessed
  ydotool fallback. Input failure does not block display-only release.
- **Phone decode and bridge throughput:** an unsupported phone has a clear state. If the intended target phone
  fails MSE or transport feasibility, stop and revise the design rather than silently adding a native decoder.
- **Output identity/placement:** the spike observed placement once, not stable placement across sessions. Test
  repeated close/reopen on KDE; document re-arrangement if placement is not retained. No automatic placement API.
- **Visibility churn:** closing a virtual monitor can move desktop windows. Immediate explicit close is intentional;
  document this behavior and measure it during page switching. The ten-second deadline is for dead viewers only.
- **Cost:** report real hardware/software encoder behavior. Lower fps is an explicit widget choice; do not claim
  the software path meets the hardware performance target without measurements.

## 7. Feasibility gate and rollout

### Completed desktop spike and remaining gates

The [findings](2026-11-26-linux-extended-display-spike-findings.md) record decision **revise**. The desktop path is
feasible: a real 1920×1080 virtual output, automatic removal on session close, and continuous software H.264 playback
in phone Chrome over plain HTTP/USB were observed. Neither mirror capture nor hardware encode, 15 fps operation,
pinned app playback, Wi-Fi, actual latency, congestion recovery or touch has been established. Buffer-ahead
0.15–0.53 s is not a glass-to-glass measurement or a proof that the ≤300 ms target was met.

Retained implementation may now build the missing measurement surfaces. The implementation milestones below are
not GitHub PR numbers (the existing documentation is in PR #1). Each milestone must meet its gate before release of
that surface. Failure stops that surface for correction or spec revision, not unrelated foundational work.

| Implementation milestone | Tasks in plan | Gate before that surface ships |
|---|---|---|
| 1. Helper foundation | 1–6 | Protocol/process tests; real VIRTUAL start/close; no persistence options; idle desktop remains healthy; no public endpoint |
| 2. Endpoint and pinned native bridge | 7–9 | Synthetic-media USB/Wi-Fi transport; pin/key/bridge-key rejection; bounded delivery; route switch and cleanup |
| 3. Mirror widget and MSE | 10–11 | Real MONITOR stream inside the actual app over pinned USB; real codec decode; bounded callbacks; idle display and slow-consumer recovery; demo/docs/counts |
| 4. Extend widget | 12 | Real VIRTUAL stream inside the app; consent on every new session; placement and lifecycle matrix; working 15 fps software mode |
| 5. Optional pointer | 13 | Granted device and proven logical mapping on scaled/rotated sources, including #2077; otherwise display-only |
| 6. Release verification | 14 | Real app Wi-Fi capture; ten-minute USB/Wi-Fi thermal and glass-to-glass measurements; throttling recovery; packaging/dependency/docs checks |

The ≤300 ms median remains a release target to measure, not an initial coding gate or an achieved result. Missing
VAAPI is not a blocker: prefer a usable hardware encoder when present, then openh264enc, then x264enc. Offer the
specified 10/15/30 fps choices and report the measured ~90% of one core for the spike's software 1080p30 run.
A user-selectable lower-resolution mode is not specified in v1; add one only through an explicit spec change.

Persistence is deferred from all six milestones. Do not ship a call that crashed the target portal merely to complete
a token task. Health timers must be independent of frame delivery. The spike's dropping appsink/live-edge seeking
must not replace the bounded, decodable recovery policy in §5.5. No separate player, MJPEG or unreviewed transport
is introduced if a milestone fails.

## 8. Implementation latitude

The builder may choose internal class/function names, encoder-specific property values and UI wording consistent
with these contracts. Queue tuning requires evidence and must preserve the stated bounds/recovery test. Changing
transport, token/session scope, input fallback, lifecycle deadlines or supported decoder formats requires updating
this spec. Product implementation and its detailed plan remain separate from this document revision.
