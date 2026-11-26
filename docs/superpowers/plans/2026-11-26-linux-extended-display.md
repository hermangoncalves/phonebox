# Linux Extended Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended when explicitly selected by the user) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a KDE Wayland virtual or physical monitor inside the paired phone's Screen widget, with bounded latency, reliable cleanup and optional proven absolute touch control.

**Architecture:** A Python Gio/GStreamer helper owns portal capture and H.264 fragmented MP4. The .NET agent authorizes and supervises one viewer per phone, while native Android code owns authenticated pinned transport and passes bounded media chunks to WebView MSE. Capture-only sessions are the baseline; input is a separately gated milestone.

**Tech Stack:** Existing .NET 10 / ASP.NET Core / xUnit; Python 3 / PyGObject / Gio / GStreamer / PipeWire; Kotlin / Android API 21+ / existing pinned TLS; ES modules / MSE / Playwright. No additional runtime package is assumed beyond the optional Linux capture dependencies.

**Spec:** [2026-11-26-linux-extended-display-design.md](../specs/2026-11-26-linux-extended-display-design.md). Read the entire spec before executing this plan. This document plans work; it does not record successful hardware experiments.

## Global Constraints

- “V1 excludes Windows, macOS, GNOME, X11 and wlroots support”; “more than one active screen per phone”; “more than two active sessions per agent”.
- “output above 1080p or 30 fps”; “a native video decoder”; “MJPEG”; and “automatic output placement” are excluded.
- “`control` defaults to false.” Video works without pointer permission; input requires granted permission and a validated logical mapping.
- “Target median glass-to-glass latency ≤300 ms” at “1080p30, 8 Mbit/s” under recorded conditions.
- “Explicit close stops immediately; an unresponsive viewer is stopped within 10 seconds.” Graceful helper shutdown has a two-second kill deadline.
- “Limit a chunk to 256 KiB”; “512 KiB of unacknowledged binary data” on the Android bridge; agent unsent video ≤1 MiB; JS pending appends ≤512 KiB.
- “Restore tokens are single-use and replaced on Start.” Scope them by `(device, mode, control)` and consume before use.
- “No monitor identifiers or output names are accepted from the phone.” No JavaScript credentials, hosts, ports, URLs, commands or capture pipeline options.
- Chromium 83 compatibility; Android `minSdk = 21`; retain the existing pinned USB and Wi-Fi authentication model.
- Follow CONTRIBUTING.md: feature issue first, reviewable PRs near 800 non-test changed lines, no CHANGELOG/version changes, widget registry/icon/count/demo/docs together.
- Never run hardware experiments against someone else's live session, install dependencies, publish, or merge merely because the plan lists those operations. Use the execution session's authorization and normal installer prompts.

## Execution order and review boundaries

Task 0 blocks all retained implementation. After its evidence is reviewed, execute Tasks 1–6 (helper/session foundations),
7–9 (endpoint plus pinned Android path), 10–12 (playback and working widget), 13 (optional proven touch), and 14 (release verification).
The subsystem boundaries are connected parts of one feature, not independent products. Each task carries its own red/green test cycle.
Group adjacent tasks into PRs only if they remain reviewable. Do not merge an endpoint without its authorization tests or
register a widget without its demo/docs/count changes. Use inline execution unless the user explicitly chooses delegation.

Before Task 0, inspect `git status --short`, current branch and applicable project guidance. At execution time, use the
using-git-worktrees skill if isolation is needed. Keep pre-existing spec edits and other user changes intact.

## File and ownership map

New paths below are deliberate additions, not claims that these files already exist. Paths are relative to the repository root.

| Files | Responsibility |
|---|---|
| `docs/superpowers/specs/2026-11-26-linux-extended-display-spike-findings.md` | Reproducible hardware evidence and go/no-go decision |
| `agent/PhoneBox.Agent/ScreenProtocol.cs` | Limits, framed reader/writer, public versus private events |
| `agent/PhoneBox.Agent/ScreenOptions.cs` | Normalize server-owned widget options and selection scope |
| `agent/PhoneBox.Agent/ScreenTokenStore.cs` | Private atomic token consume/replace with generation checks |
| `agent/PhoneBox.Agent/ScreenLease.cs` | Pure viewer ownership, heartbeat, retry and media epoch state |
| `agent/PhoneBox.Agent/ScreenCast.cs` | Process/socket lifetime and helper adapter, runtime Linux guard |
| `agent/PhoneBox.Agent/Linux/phonebox-screencast.py` | Portal request state machine, pipeline and input dispatch |
| `agent/PhoneBox.Agent/ScreenFeeds.cs` | Device/widget authorization, two-session cap and orchestration |
| `agent/PhoneBox.Agent/ScreenEndpoint.cs` | WebSocket relay and recovery POST, registered in Program.cs |
| `agent/PhoneBox.Agent.Tests/Screen*Tests.cs` | Isolated .NET tests with fake clock/config/helper |
| `agent/PhoneBox.Agent.Tests/Fixtures/screen-helper.py` | Deterministic protocol/process test peer |
| `agent/PhoneBox.Agent.Tests/Fixtures/screen-init.mp4`, `screen-media.m4s` | Tiny self-generated H.264 fixture; no desktop content |
| `agent/PhoneBox.Agent.Tests/Fixtures/screen-fixture.json` | Codec, generation command, license and fixture digest |
| `agent/PhoneBox.Agent.Tests/test_screen_helper.py` | Python tests without Gio/Gst installation |
| `android/app/src/main/java/net/oxog/phonebox/deskpanel/ScreenSocket.kt` | Pinned WebSocket transport and recovery request |
| `android/app/src/main/java/net/oxog/phonebox/deskpanel/ScreenCredit.kt` | Pure byte-credit and stale-handle accounting |
| `android/app/src/main/assets/www/widgets/screen-media.js` | MSE and append/backlog accounting |
| `android/app/src/main/assets/www/widgets/screen-input.js` | Contained-video mapping and gesture state |
| `android/app/src/main/assets/www/widgets/screen.js` | Widget UI, visibility and bridge callbacks |
| `tools/ui-tests/screen*.spec.js`, `screen-fixture.js` | Mock bridge, playback/lifecycle/input regressions |
| `tools/pages/screen-demo.js` | Demo-only bridge and media fixture adapter |

Existing integration points: `Program.cs`, `Lan.cs`, `Config.cs`/ConfigStore save notifications, `AppPaths.cs`,
`PanelWidgets.cs`, `PhoneBox.Agent.csproj`, `AgentLink.kt`, `MainActivity.kt`, panel `main.js`, `style.css`, `locale*.js`,
`widgets.json`, `tools/pages/build.py`, `tools/pages/demo.js`, `setup.sh`, `README.md`, `docs/GUIDE.md`, `docs/SETUP.md`, `PRIVACY.md`.
Do not refactor trackpad transport or the existing camera widget as part of this feature.

## Wire contract used by all tasks

All helper JSON events use a `type` discriminator; commands use `op`. Public server events are serialized explicitly,
not forwarded from arbitrary helper dictionaries. Width/height are negotiated video pixels; logical input extents stay private.
Widget/viewer identity is fixed by the upgraded connection, so input messages cannot retarget another widget.

| Direction | JSON shape | Validation/handling |
|---|---|---|
| Server → phone | `{type:"ready",width,height,codec,inputAvailable}` | No restore token or desktop position |
| Server → phone | `{type:"media",epoch}` | Clear decoder state before accepting this epoch's binary chunks |
| Server → phone | `{type:"state",code}` | Fixed codes from spec §5.7 |
| Server → phone | `{type:"stats",fps,bitrate}` | Bounded numeric telemetry for this local viewer only |
| Phone → server | `{op:"heartbeat"}` | Active connection generation; every two seconds |
| Phone → server | `{op:"report",epoch,sequence,lagMs}` | Twice per second; lagMs may be null before playable data |
| Phone → server | `{op:"pointer",x,y}` | Finite normalized numbers, exact current widget consent |
| Phone → server | `{op:"button",n,down}` | n = 1/2/3 for left/right/middle; down boolean |
| Phone → server | `{op:"scroll",dx,dy}` | Finite numbers clamped to ±120 |
| Phone → server | `{op:"releaseAll"}` | Release only this viewer's held buttons |
| Phone → server | `{op:"chooseMonitor"}` | Current mirror viewer only; clear scope and restart selection |
| Native HTTP POST | `{op:"recover",viewer}` | Same path with widget query; 1 KiB body; current viewer only |

Native `screenAck` releases local bridge credit; it is not another public WebSocket operation. `report` informs the
agent of append progress and playback lag. Native supplies the JSON via the existing keyed `screenSend` wrapper.
Helper permission events use `{type:"permission",restore_token}` and are consumed only by ScreenFeeds. Helper
start/reset/stop/input commands follow spec §5.3. The agent obtains logical mapping from the helper without exposing
it as public state. Binary WebSocket messages contain Task 1 frames; native strips framing before base64 delivery.
Epoch and sequence are unsigned 32-bit values; start a fresh session before either wraps. JavaScript viewer handles
are positive safe integers, and native validates the same range before converting to Long.

## Task 0: Prove the full target path before retained implementation

**Files:** Create only the findings document listed above. Keep experiment source in an isolated temporary directory/worktree.
**Consumes:** Spec §7, target Linux desktop and paired target phone.
**Produces:** Recorded evidence for each gate; selected installed encoder properties/caps and codec; decision on touch eligibility.

- [ ] Record date, distro, session, portal capabilities, Python imports, Gst plugin inspection, GPU/driver and Android/WebView versions.
  Use read-only discovery first:
  ```bash
  printf '%s\n' "$XDG_SESSION_TYPE" "$XDG_CURRENT_DESKTOP"
  gdbus introspect --session --dest org.freedesktop.portal.Desktop --object-path /org/freedesktop/portal/desktop
  gst-inspect-1.0 pipewiresrc
  gst-inspect-1.0 vah264enc
  gst-inspect-1.0 x264enc
  gst-inspect-1.0 mp4mux
  python3 -c 'import gi; gi.require_version("Gst", "1.0"); from gi.repository import Gio, Gst; print(Gst.version_string())'
  ```
  Record missing capabilities rather than treating a failed inspection as proof that capture is impossible.
- [ ] Build a throwaway combined desktop/Android experiment following spec §5.2–5.5. Use the actual panel asset origin,
  a keyed native bridge, existing pin/key handling, and MSE. Request consent on the computer. Prove extend and mirror;
  do not replace this test with desktop-browser playback or an unpinned LAN server.
- [ ] Exercise ten start/stop cycles per selection scope, both routes, denial, app background/kill, unplug, helper death,
  desktop restart and ten-second throttling. Record output removal time, token rotation and output placement.
- [ ] Measure ten minutes per route at 1080p30/8 Mbit/s, latency median/p95, bridge byte counts/credit, CPU/GPU/memory,
  software/15 fps fallback, and recovery ≤500 ms lag within five seconds or explicit stop. Test pointer logical mapping
  on virtual and mirrored HiDPI outputs, rotation and negative desktop placement. Record #2077 behavior.
- [ ] Write the findings with headings: Inventory, Reproduction, Pipeline/caps, USB results, Wi-Fi results, Lifecycle,
  Persistence, Congestion, Input mapping, Decision. Every claimed pass links to a recorded measurement or transcript.
  State `proceed`, `revise`, or `display-only`; failures in spec gate items 1–6 require a spec revision before Task 1.
- [ ] Review the evidence with the user before retained implementation. Commit only the approved findings document:
  `git add docs/superpowers/specs/2026-11-26-linux-extended-display-spike-findings.md`, then
  `git commit -m "docs: record Linux screen feasibility results"`.

## Task 1: Define and test the framed media boundary

**Files:** Create `ScreenProtocol.cs`, `ScreenProtocolTests.cs`, fixture files; modify `PhoneBox.Agent.Tests.csproj` to copy fixtures.
**Interfaces:**
```csharp
internal sealed record ScreenFrame(byte Kind, byte[] Payload);
internal static class ScreenProtocol {
    public const int MaxJson = 64 * 1024, MaxVideo = 256 * 1024;
    public static Task<ScreenFrame?> ReadAsync(Stream input, CancellationToken ct);
    public static Task WriteAsync(Stream output, ScreenFrame frame, CancellationToken ct);
    public static byte[] Video(uint epoch, uint sequence, ReadOnlySpan<byte> mp4);
}
```
`ReadAsync` returns null only for clean EOF before a header; truncation is InvalidDataException.
Video's epoch/sequence header counts inside MaxVideo, so MP4 slices are at most MaxVideo minus eight bytes.
JSON messages are UTF-8, strict known event/operation types. A `permission` event is never a public event.

- [ ] Write the first regression:
  ```csharp
  [Fact]
  public async Task TruncatedHeaderIsNotCleanEof() {
      using var input = new MemoryStream(new byte[] { 2, 0, 0 });
      await Assert.ThrowsAsync<InvalidDataException>(() => ScreenProtocol.ReadAsync(input, default));
  }
  ```
  Add separate cases for unknown kind, advertised oversized length before allocation, byte-by-byte reads, truncated payload,
  cancellation and network-byte-order epoch/sequence round trip.
- [ ] Run `dotnet test agent/PhoneBox.Agent.Tests --filter FullyQualifiedName~ScreenProtocolTests`; confirm the intended missing-contract failure.
- [ ] Implement exact-length reads, bounds before allocation and big-endian fields using existing .NET primitives.
  Generate a two-second synthetic pattern fixture using the spike's proven encoder/mux settings; save init/media separately
  with codec/digest/generation command in screen-fixture.json. Never record the desktop. Assert the fixture begins with valid
  init and media boxes and supports the spike's codec string.
- [ ] Rerun focused tests. Copy fixtures to output using `None Update="Fixtures/**" CopyToOutputDirectory="PreserveNewest"`.
  Add a whitelist serializer that strips permission/token events and test with a distinctive secret string.
- [ ] Commit the protocol, fixtures and tests: `feat: define bounded screen media protocol`.

## Task 2: Normalize options and define selection identity

**Files:** Create `ScreenOptions.cs`, `ScreenOptionsTests.cs`.
**Interfaces:**
```csharp
internal sealed record ScreenOptions(string Mode, int Fps, int Bitrate, string Cursor, bool Control) {
    public static ScreenOptions From(WidgetConfig widget);
}
internal readonly record struct ScreenScope(string Device, string Mode, bool Control);
```
No settings come from a socket message. Unknown mode/fps/quality/cursor normalize to extend/30/medium/embedded.
Use `PanelWidgets.Find(config, device, widgetKey, "screen")` for the exact device page and enabled stack-card lookup;
retain the repo's enabled-widget consent rule and never borrow control from another Screen or trackpad widget.

- [ ] Add a test using the actual WidgetConfig option representation:
  ```csharp
  [Fact]
  public void MissingOptionsDoNotGrantInput() {
      var o = ScreenOptions.From(new WidgetConfig { Type = "screen" });
      Assert.Equal("extend", o.Mode);
      Assert.Equal(30, o.Fps);
      Assert.Equal(8_000_000, o.Bitrate);
      Assert.False(o.Control);
  }
  ```
  Table-test fps 10/15/30, rejection of 60, quality 4/8/16 Mbit/s, and malformed option types.
- [ ] Run `dotnet test agent/PhoneBox.Agent.Tests --filter FullyQualifiedName~ScreenOptionsTests` and observe red.
- [ ] Implement normalization and typed scope. Use ordinal device IDs; do not derive identity from labels or output position.
- [ ] Rerun tests; prove no conversion accepts arbitrary pipeline strings or throws on corrupt stored options.
- [ ] Commit: `feat: normalize screen widget capture options`.

## Task 3: Implement ownership and token storage as isolated state

**Files:** Create `ScreenLease.cs`, `ScreenLeaseTests.cs`, `ScreenTokenStore.cs`, `ScreenTokenStoreTests.cs`.
**Interfaces:**
```csharp
internal sealed class ScreenLease(string widgetKey, long viewer, long nowMs) {
    public long Viewer { get; }
    public uint Epoch { get; }
    public bool Heartbeat(long viewer, long nowMs);
    public bool Expired(long nowMs);
    public bool Replace(long expectedViewer, long replacementViewer, long nowMs);
    public bool Close(long viewer);
}
internal sealed record ScreenTokenLease(ScreenScope Scope, long Generation, string? Token);
internal sealed class ScreenTokenStore(string path) {
    public ScreenTokenLease Consume(ScreenScope scope);
    public bool Replace(ScreenTokenLease lease, string token);
    public void Clear(ScreenScope scope);
    public void RemoveDevice(string device);
}
```
Consumed tokens are deleted durably before helper startup. Generation checks reject delayed permission events after replacement/clear.
Use a private sidecar `Path.Combine(AppPaths.ConfigDir, "screen-tokens.json")` so token rotation cannot overwrite concurrent
settings edits. This is internal secret metadata, not a new global setting or public config field. Use the same AppPaths
owner-only protections, write a private temporary file, flush, atomically rename, and fail closed on persistence failure.
The sidecar uses the spec’s existing-config owner-only protection requirement without adding token fields to editable settings; document its path in SETUP.

- [ ] Add deterministic tests, including:
  ```csharp
  [Fact]
  public void OldViewerCannotExpireOrCloseItsReplacement() {
      var lease = new ScreenLease("desk/screen", 1, 0);
      Assert.True(lease.Replace(1, 2, 2_000));
      Assert.False(lease.Close(1));
      Assert.True(lease.Heartbeat(2, 9_000));
      Assert.False(lease.Expired(18_999));
      Assert.True(lease.Expired(19_000));
  }
  ```
  Token tests use a disposable directory: reopen after Consume sees no reusable token; stale Replace fails;
  mirror/extend and control scopes remain separate; Clear invalidates outstanding leases; another device is unaffected.
- [ ] Run `dotnet test agent/PhoneBox.Agent.Tests --filter 'FullyQualifiedName~ScreenLeaseTests|FullyQualifiedName~ScreenTokenStoreTests'` and observe red.
- [ ] Implement locking and durable token transitions. Handle a missing file as empty, corrupt storage as no restorable selection,
  and an unwritable store as start failure before token reuse. Never include token values in exceptions/logs.
- [ ] Verify tests, Unix file permissions, atomic replacement failure, concurrent Consume calls and agent restart.
  A second Consume may yield no token; it must never yield the same token twice.
- [ ] Commit: `feat: track screen viewer leases and private portal tokens`.

## Task 4: Build the portal helper state machine without an encoder

**Files:** Create `Linux/phonebox-screencast.py`, `PhoneBox.Agent.Tests/test_screen_helper.py`.
**Interfaces:** Python `PortalSession(bus, emit)` exposes `start(options)`, `stop()`, `release_all()`; `bus` is a small
adapter with `request(interface, method, args, callback)` and `close_session(handle)`. Production adapter uses Gio;
tests inject a fake recorder. `emit` sends the Task 1 event dictionaries. Keep gi imports in the production adapter
so unittest can import protocol/state code without installed capture libraries.

- [ ] Create an importlib-based unittest loader for the hyphenated helper filename and FakeBus that records method calls,
  lets tests complete Request responses, and records session closure. Start with:
  ```python
  def test_capture_only_does_not_select_input(self):
      bus = FakeBus()
      session = helper.PortalSession(bus, lambda event: None)
      session.start(dict(mode="mirror", control=False, fps=30, bitrate=8000000,
                         cursor="embedded", restore_token=None))
      bus.complete_create("/session/one")
      self.assertEqual(bus.calls[-1][0:2],
                       ("org.freedesktop.portal.ScreenCast", "SelectSources"))
  ```
  FakeBus.complete_create invokes the stored create callback with a successful session_handle result.
  Add combined-session tests requiring SelectDevices before SelectSources, persistence on the correct interface,
  granted-device checks, permission emission before encoding, denial without retry and cancellation during approval.
- [ ] Run `python3 -m unittest discover -s agent/PhoneBox.Agent.Tests -p 'test_screen_helper.py' -v`; confirm red without requiring gi.
- [ ] Implement asynchronous Request response handling and Session.Closed observation. Subscribe before invoking requests
  to avoid fast-response races. Close outstanding request handles on cancellation. Validate source/cursor bits first;
  close PipeWire FD and session exactly once on every failure. Never assume CreateSession accepts devices/persistence.
- [ ] Rerun tests including simulated late responses after stop, absent optional size/position and user cancellation.
- [ ] Commit: `feat: implement screen portal session lifecycle`.

## Task 5: Encode fragmented MP4 and supervise the helper process

**Files:** Modify helper and `PhoneBox.Agent.csproj`; create `ScreenCast.cs`, `ScreenCastTests.cs`, `Fixtures/screen-helper.py`.
**Interfaces:**
```csharp
internal interface IScreenRun : IAsyncDisposable {
    Task SendAsync(object command, CancellationToken ct);
}
internal delegate Task<IScreenRun> ScreenLauncher(
    ScreenOptions options, ScreenTokenLease token,
    Func<ScreenFrame, Task> receive, CancellationToken ct);
```
ScreenCast supplies `static Task<IScreenRun> StartAsync(string helperPath, string runtimeDirectory,
ScreenOptions options, ScreenTokenLease token, Func<ScreenFrame, Task> receive, CancellationToken ct)`;
the production ScreenLauncher closes over the embedded helper path. Tests supply the fake helper path.
Receive is ordered and may apply bounded backpressure.
Use frame/event contracts from Tasks 1 and 4. The fake helper supports success, truncated output, stall and crash modes
selected by the test process argument; it does not open a portal.

- [ ] Write tests launching the fake helper from a temporary path containing spaces: ready followed by media, clean stop,
  oversized output, child death, parent socket loss and a stall killed after the two-second shutdown deadline.
  The first invariant test should assert that DisposeAsync closes the fake peer's socket and that its process exits.
  Inject process launch/runtime paths and timeout scheduling; ordinary tests must not touch installed settings.
  Define `ScreenCastTestPeer.StartAsync(mode)` in ScreenCastTests.cs: it copies the fake helper into a disposable
  directory containing spaces, calls ScreenCast.StartAsync, and exposes `Run` and a `Task Exited` completed when the
  fake process exits. Its DisposeAsync closes Run and deletes only its temporary directory. Then test:
  ```csharp
  [Fact]
  public async Task DisposeStopsThePeer() {
      await using var peer = await ScreenCastTestPeer.StartAsync("success");
      await peer.Run.DisposeAsync();
      await peer.Exited.WaitAsync(TimeSpan.FromSeconds(3));
      Assert.True(peer.Exited.IsCompletedSuccessfully);
  }
  ```
- [ ] Run `dotnet test agent/PhoneBox.Agent.Tests --filter FullyQualifiedName~ScreenCastTests` and observe red.
- [ ] Implement private runtime directory/socket creation (0700/0600), resource extraction, ArgumentList launch, deadlines,
  cancellation and kill-tree fallback. Runtime-guard Linux before probing/extraction. The shared net10.0 target also builds
  for macOS; do not compile out types that Program.cs references or infer runtime OS from the build host.
- [ ] Add the Gst pipeline proven in Task 0: negotiated downscale, bounded leaky raw queue before encoding, low-latency
  VAAPI with software fallback, H.264 codec from actual caps, fragmented MP4 appsink. Implement resetMedia by restarting
  encoder/mux while retaining the portal and PipeWire session. Emit a new epoch then init and keyframe media. Add Python
  fake-pipeline tests for fallback and cleanup; run the real pipeline on hardware only in the manual matrix.
- [ ] Rerun ScreenCast/Python tests and `dotnet build agent/PhoneBox.Agent -f net10.0`.
  Commit: `feat: encode and supervise Linux screen capture`.

## Task 6: Orchestrate device ownership, consent revocation and retries

**Files:** Create `ScreenFeeds.cs`, `ScreenFeedsTests.cs`; modify Program.cs dependency registration/settings-save/shutdown hooks.
**Interfaces:**
```csharp
internal sealed record ScreenViewer(string Device, string Widget, long Handle);
internal sealed record ScreenConnection(ScreenViewer Viewer, long Generation);
internal sealed record ScreenOpenResult(string State, ScreenConnection? Connection);
// ScreenFeeds constructor accepts ConfigStore, ScreenTokenStore, ScreenLauncher and Func<long> nowMs.
// Internal methods; register the owning concrete service in DI:
// Task<ScreenOpenResult> OpenAsync(ScreenViewer viewer, Func<ScreenFrame, Task> emit, CancellationToken ct)
// Task CloseAsync(ScreenConnection connection)
// Task HeartbeatAsync(ScreenConnection connection)
// Task SettingsSavedAsync()
// Task TickAsync()
// Task InputAsync(ScreenConnection connection, string json, CancellationToken ct)
```
ScreenFeeds is IAsyncDisposable. Add PrepareRecoveryAsync/ResetMediaAsync in Task 7. One lock protects ownership;
never await process work while holding it. Every successful OpenAsync allocates a fresh internal connection generation,
even when the client viewer handle is unchanged during recovery. Endpoint read/send/finally closures retain that
ScreenConnection; CloseAsync and input/heartbeat calls reject stale generations. The public viewer handle alone is
insufficient to guard old socket callbacks. The helper media epoch is a third, separate counter.

- [ ] Test with temporary ConfigStore and injected fake launcher/clock: different widget on same phone is busy;
  same widget replacement closes previous owner; third device is busy; hidden/removed widget cannot open.
  Use actual pages, PhoneConfig page selection and Smart Stack cards. No permission is borrowed from another widget.
  Define `ScreenFeedsTestRig.Create()` in ScreenFeedsTests.cs to create a temporary ConfigStore with phone-a and phone-b,
  enabled widgets desk/screen and desk/other, in-memory token path, fake ScreenLauncher and writable NowMs clock.
  Expose `Feeds` and dispose it before deleting the directory. Use a completed callback for emitted frames:
  ```csharp
  [Fact]
  public async Task AnotherWidgetCannotTakeTheSamePhoneSlot() {
      await using var rig = ScreenFeedsTestRig.Create();
      var first = await rig.Feeds.OpenAsync(new("phone-a", "desk/screen", 1),
          _ => Task.CompletedTask, default);
      Assert.NotNull(first.Connection);
      var second = await rig.Feeds.OpenAsync(new("phone-a", "desk/other", 2),
          _ => Task.CompletedTask, default);
      Assert.Equal("busy", second.State);
      Assert.Null(second.Connection);
  }
  ```
- [ ] Run `dotnet test agent/PhoneBox.Agent.Tests --filter FullyQualifiedName~ScreenFeedsTests` and observe red.
- [ ] Implement reserve/launch/recheck ownership, rollback after failed launch, heartbeat expiration, token consumption
  before launch and immediate private permission-event persistence. Call SettingsSavedAsync after every successful
  config save that can change pages/phones/options. Compare removed phone records and call RemoveDevice.
  Do not log tokens or forward helper permission events. Cancel stale startup requests and approval dialogs.
- [ ] Add fake-clock tests for ten-second expiry, shutdown disposal, 1/2/4/8/30-second crash retries, cancellation of retries
  after hide and no automatic denied/missing-dependency retry. Config disable/control change must release input and close
  capture before the next message can act. Rerun the focused suite and existing PanelWidgets/Camera tests.
- [ ] Commit: `feat: own screen sessions by enabled phone widget`.

## Task 7: Add the authenticated endpoint and recoverable media epochs

**Files:** Create `ScreenEndpoint.cs`, `ScreenEndpointTests.cs`, `ScreenRecoveryTests.cs`; modify Program.cs and Lan.cs;
extend ScreenFeeds.cs and ScreenLease.cs.
**Interfaces:** `static Task ScreenEndpoint.HandleAsync(HttpContext context)` resolves ScreenFeeds from DI. GET upgrades at
`/api/screen?widget=<URL-encoded-widget-key>&viewer=<integer>`; POST at the same path accepts `{op:"recover",viewer:<integer>}` with a 1 KiB body cap.
Add `Task<bool> PrepareRecoveryAsync(ScreenViewer viewer)` and `Task ResetMediaAsync(ScreenViewer viewer)` to ScreenFeeds.
Preparation is idempotent for a matching active viewer and marks retention until its unchanged heartbeat deadline.
Use Task 1 binary framing inside each binary WebSocket message; reassemble fragmented WebSocket messages with limits.

- [ ] Add endpoint tests using ASP.NET's existing DefaultHttpContext plus an injected IWebSocketFeature test peer; keep
  RequestGate tests separate so an endpoint-level pass is not mistaken for auth validation. Test invalid widget/device,
  unsupported OS, malformed operations, body/message caps, foreign recovery handle, permission secret filtering and
  non-upgrade GET rejection. Extend existing gate/allow-list tests for bearer key, origin, USB and Wi-Fi behavior.
  Use an explicit 400 response for an authorized non-upgrade GET. A focused test creates DI with the fake ScreenFeeds
  rig and sets path/query/device headers on DefaultHttpContext before calling ScreenEndpoint.HandleAsync:
  ```csharp
  [Fact]
  public async Task ScreenGetRequiresAWebSocketUpgrade() {
      await using var rig = ScreenFeedsTestRig.Create();
      using var services = new ServiceCollection().AddSingleton(rig.Feeds).BuildServiceProvider();
      var ctx = new DefaultHttpContext { RequestServices = services };
      ctx.Request.Method = "GET";
      ctx.Request.Path = "/api/screen";
      ctx.Request.QueryString = new QueryString("?widget=desk%2Fscreen&viewer=1");
      ctx.Request.Headers["X-PhoneBox-Device"] = "phone-a";
      await ScreenEndpoint.HandleAsync(ctx);
      Assert.Equal(400, ctx.Response.StatusCode);
  }
  ```
  Define ScreenFeedsTestRig as a top-level internal class in its test file so the endpoint/recovery suites reuse it; use a service-provider
  ownership arrangement that disposes Feeds once, or make its disposal idempotent.
- [ ] Run `dotnet test agent/PhoneBox.Agent.Tests --filter 'FullyQualifiedName~ScreenEndpointTests|FullyQualifiedName~ScreenRecoveryTests'` and observe red.
- [ ] Implement the route after existing RequestGate. Require a nonempty bounded device ID and integer viewer handle;
  bind it to the current authorized connection, never treat it as an authentication credential. Derive capture options
  on the server. Parse fixed operation shapes only, reject extra fields, cap input at 120 messages/second and text at
  4 KiB, and reject NaN/infinity. Cursor/source changes require session restart.
- [ ] Add a 1 MiB bounded unsent media queue with two-second send timeout. Recovery cancels old send/read loops,
  discards old queued data, retains the portal only after successful preparation, and atomically replaces the viewer
  transport using ScreenConnection.Generation checks. Call resetMedia only after replacement transport attaches; release held input first. New epoch must arrive
  before media, and start with init/keyframe. Reject delayed old frames and reset the decoder-relevant counters.
- [ ] Test overflow, partial-send stall, repeated preparation, old socket finally racing replacement, prepare then hide,
  config disable during retention, unreplaced retention timeout, and three resets within 30 seconds stopping the session.
  Rerun all Screen and RequestGate/Lan/UsbLink tests. Commit: `feat: relay authorized screen media with bounded recovery`.

## Task 8: Build pinned Android streaming and byte credit

**Files:** Create `ScreenSocket.kt`, `ScreenCredit.kt`,
`android/app/src/test/java/net/oxog/phonebox/deskpanel/ScreenCreditTest.kt`,
`android/app/src/androidTest/java/net/oxog/phonebox/deskpanel/ScreenSocketTest.kt`; modify AgentLink.kt.
**Interfaces:**
```kotlin
internal class ScreenCredit(private val limit: Int = 512 * 1024) {
    fun reserve(viewer: Long, epoch: Long, sequence: Long, bytes: Int): Boolean
    fun acknowledge(viewer: Long, epoch: Long, sequence: Long): Boolean
    fun reset(viewer: Long, epoch: Long)
    val pendingBytes: Int
}
internal class ScreenSocket(private val link: AgentLink,
    private val deliver: (Long, String, String?) -> Unit) {
    fun open(widget: String, viewer: Long)
    fun close(viewer: Long)
    fun closeAll()
    fun send(viewer: Long, message: String)
    fun acknowledge(viewer: Long, epoch: Long, sequence: Long)
    fun recover(viewer: Long)
}
```
`deliver(viewer, eventJson, base64OrNull)` runs through MainActivity's main-frame dispatch added in Task 9. Event JSON
contains epoch/sequence for binary chunks, never credentials. A single-thread executor owns connection state; network I/O
and base64 conversion never block the UI thread. Stop/route change must interrupt blocked reads and invalidate queued callbacks.

- [ ] Start with a JVM byte-credit test:
  ```kotlin
  @Test fun staleAcknowledgementCannotReleaseNewViewerCredit() {
      val c = ScreenCredit(16)
      c.reset(2, 1)
      assertTrue(c.reserve(2, 1, 0, 12))
      assertFalse(c.acknowledge(1, 1, 0))
      assertEquals(12, c.pendingBytes)
      assertFalse(c.reserve(2, 1, 1, 8))
  }
  ```
  Also test duplicate/out-of-order acknowledgement, old epoch, oversized chunk and reset while delivery is pending.
- [ ] Run `(cd android && ./gradlew testDebugUnitTest --tests '*ScreenCreditTest')`; confirm red.
- [ ] Implement credit and an authenticated binary WebSocket client. Reuse AgentLink's current USB key, active route,
  headers and pinned TLS factory; extract only a narrow internal route snapshot if needed. Do not assume the trackpad's
  Wi-Fi-only implementation covers USB. Validate 101 response and Sec-WebSocket-Accept, server masking rules, control
  frames, frame/message lengths and continuation frames. Serialize masked client writes including pongs and close.
  Use byte-oriented handshake parsing; a BufferedReader can read past the headers and swallow initial binary bytes.
- [ ] Implement recover through native `link.request("POST", path, body, "application/json")`, consuming/closing the Reply body.
  MainActivity.proxy does not forward POST bodies; JavaScript fetch is not the recovery path. On success close old WSS,
  reconnect same viewer, clear credits and ignore old callbacks. On failure close normally and report lost. CloseAll on
  every route/computer change, AgentLink.close and Activity pause/destroy. Explicit close is never a recovery retention.
- [ ] Add instrumentation tests following TlsConnectionReuseTest's isolated pinned loopback server: both route header
  sets, wrong pin/key, handshake with immediate binary bytes, fragmented/control frames, bounded stalled consumer,
  route switch and close during blocked read. Use public test-only cert fixtures, never production keys.
  Run `(cd android && ./gradlew testDebugUnitTest lintDebug assembleDebug connectedDebugAndroidTest)` with a test device.
  Commit: `feat: stream screen media through pinned Android transport`.

## Task 9: Expose a keyed main-frame bridge with bounded callbacks

**Files:** Modify MainActivity.kt, AgentLink.kt, android/app/build.gradle.kts and panel main.js; create
`android/app/src/androidTest/java/net/oxog/phonebox/deskpanel/ScreenBridgeTest.kt`;
extend `tools/test-bridge-checks.py` and `tools/kotlin-tests/BridgeChecks.kt` if their native bridge allow-list requires it.
**Interfaces:** Extend MainActivity.Bridge with these JavaScript entry points:
```kotlin
@JavascriptInterface fun screenOpen(key: String, widget: String, viewer: Long)
@JavascriptInterface fun screenClose(key: String, viewer: Long)
@JavascriptInterface fun screenSend(key: String, viewer: Long, message: String)
@JavascriptInterface fun screenAck(key: String, viewer: Long, epoch: Long, sequence: Long)
@JavascriptInterface fun screenRecover(key: String, viewer: Long)
```
Expose the existing main.js keyed bridge wrapper's matching names without key arguments. Deliver only to
`window.onScreenEvent(viewer, eventJson, base64OrNull)` using JSON serialization of every string argument, not string interpolation.
Widget subscription/dispatch is implemented in Task 11; when no subscriber exists, close that viewer rather than accumulate data.

- [ ] Add instrumentation assertions exercising wrong key, opaque iframe, valid main frame and Activity lifecycle.
  Test argument strings containing quotes/newlines to prove they cannot escape evaluateJavascript. Assert that an event
  queued before a viewer closes cannot invoke its replacement's subscriber.
  Define a test-only ScreenBridgeTestRig using ActivityScenario, isolated pairing preferences and Task 8's loopback
  server; expose `evaluate(script)`, `awaitUiIdle()` and `connections` (accepted server sockets). Its setup enables a
  valid paired route, and disposal restores preferences and closes Activity/server. Exercise the real JS interface:
  ```kotlin
  @Test fun wrongKeyCannotOpenScreen() {
      ScreenBridgeTestRig().use { rig ->
          rig.evaluate("Android.screenOpen('wrong-key', 'desk/screen', 1)")
          rig.awaitUiIdle()
          assertEquals(0, rig.connections)
      }
  }
  ```
  A paired valid-key control case must open the server socket, so a missing route cannot make the negative test pass
  accidentally. Obtain the valid key through the main-frame wrapper; never add a production key getter. Add a direct
  activity-test dependency only if the existing androidx.test dependencies do not expose ActivityScenario.
- [ ] Run `(cd android && ./gradlew connectedDebugAndroidTest -Pandroid.testInstrumentationRunnerArguments.class=net.oxog.phonebox.deskpanel.ScreenBridgeTest)`; confirm intended red on a device.
- [ ] Implement key checks, widget-key length cap (512 characters), integer viewer validation and method allow-list.
  Parse screenSend's operation shape before native forwarding; no generic socket or URL API. Check current main-frame
  asset URL before delivery, closed/destroyed flags and viewer generation. Count pending callbacks as reserved credit;
  retain no unbounded Handler queue of encoded media. Close native transport onPause even if JS timers are frozen.
- [ ] Run bridge instrumentation and `python3 tools/test-bridge-checks.py`; rerun existing native bridge checks.
  Verify main.js still wraps every pre-existing bridge method correctly. No screen callback/data reaches custom HTML frames.
- [ ] Commit: `feat: expose keyed screen transport to the panel`.

## Task 10: Implement MSE playback and deterministic reset tests

**Files:** Create `widgets/screen-media.js`, `tools/ui-tests/screen-media.spec.js`, `tools/ui-tests/screen-fixture.js`.
**Interfaces:**
```javascript
// screen-media.js exports:
export function createScreenMedia(video, { ack, recover, unsupported }) {}
// Return: { ready({codec,width,height}), media({epoch}), chunk({epoch,sequence,bytes}),
//           lagMs(), destroy() }. bytes is Uint8Array, not base64.
```
`ack(epoch, sequence)` fires only after updateend for the corresponding append; `recover()` requests a coordinated
transport reset, not a new portal session. `unsupported(code)` closes capture at the widget boundary. lagMs is buffered
end minus video.currentTime when playable, otherwise null; do not fabricate an absolute capture timestamp.

- [ ] Write a browser test using a stub SourceBuffer that exposes append completion explicitly. Provide it by replacing
  MediaSource in addInitScript; record calls and advance updateend from the test. Test append serialization, caps and
  old epochs independently of real codec support. Example assertion:
  ```javascript
  test('an old epoch is never acknowledged into a new media stream', async ({ page }) => {
    await installScreenMediaFixture(page); // defined in screen-fixture.js in this step
    await page.goto('/panel/');
    const result = await page.evaluate(async () => {
      const { createScreenMedia } = await import('/panel/widgets/screen-media.js');
      const seen = [];
      const player = createScreenMedia(document.createElement('video'), {
        ack: (epoch, sequence) => seen.push([epoch, sequence]),
        recover() {}, unsupported() {},
      });
      player.media({ epoch: 2 });
      player.chunk({ epoch: 1, sequence: 0, bytes: new Uint8Array([0]) });
      player.destroy();
      return seen;
    });
    expect(result).toEqual([]);
  });
  ```
  `installScreenMediaFixture(page)` creates window.__screenMediaFixture with controlled sourceopen/updateend/error events,
  recorded append/remove calls and buffered ranges. Keep its fake behavior separate from the real decode test.
- [ ] Run `(cd tools/ui-tests && npx playwright test screen-media.spec.js)`; confirm red.
- [ ] Implement muted/playsInline, feature/profile checks, serialized append/remove, ≤512 KiB pending queue, two-second
  played-data trim and ≤one-second ahead policy. Handle empty ranges, sourceopen races, QuotaExceededError, play()
  rejection and destroy during append. When ahead >500 ms request recovery; never drop reference frames or seek through
  arbitrary incomplete media. Only one recovery may be pending at a time.
- [ ] Test resetting clears queues/object URLs and stale updateend callbacks, invalid MSE/codec shows unsupported and
  overflow invokes recovery without growing memory. Task 10 uses the stubbed buffer only; Task 11 adds the real
  MediaSource decode test after its fixture asset plumbing exists. That test must advance video.currentTime, not merely
  observe append calls. Keep that test in screen-media.spec.js and enable it in Task 11, without merging a skipped test.
- [ ] Rerun focused tests. Commit: `feat: play bounded screen media in WebView MSE`.

## Task 11: Register a working mirror widget and demo together

**Files:** Create `widgets/screen.js`, `tools/pages/screen-demo.js`, `tools/ui-tests/screen.spec.js`; modify
main.js, style.css, widgets.json, tools/pages/build.py, tools/pages/demo.js, README.md, docs/GUIDE.md, docs/SETUP.md,
PRIVACY.md if its existing desktop-content wording is insufficient; add localized Screen strings through locale files.
**Interfaces:** `W.screen.create(box, {o,page,w})` returns `{update(), destroy()}`. It uses Task 10's createScreenMedia,
the Task 9 wrapper and one viewer-scoped callback dispatcher. The demo implements the identical bridge methods and
`window.onScreenEvent` events; it is never injected into the production Android page.

- [ ] Create `showScreen(page, options)` in screen-fixture.js using the camera test's localStorage configuration pattern:
  disabled night rules, one enabled page and `{id:'screen',type:'screen',options}`. Supply a demo bridge with open/close/
  input counters before module startup. Add tests:
  ```javascript
  test('Stop closes capture until Retry or a visibility transition', async ({ page }) => {
    await showScreen(page, { mode: 'mirror', control: false });
    await expect(page.locator('.screen-status')).toHaveText('Live');
    await page.getByRole('button', { name: 'Stop', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.__screenDemo.active)).toBe(false);
    await page.waitForTimeout(2200);
    expect(await page.evaluate(() => window.__screenDemo.opens)).toBe(1);
    await page.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.__screenDemo.opens)).toBe(2);
  });
  ```
  Define __screenDemo state in screen-demo.js; expose counters only in the demo.
- [ ] Run `(cd tools/ui-tests && npx playwright test screen.spec.js)` and observe red.
- [ ] Implement state UI with textContent, video containment and accessible buttons. Reuse camera's visibility predicate
  but close immediately on page/stack/night/document/app pause and destruction. Add explicit visibilitychange handling;
  update() must not restart a manually stopped, still-visible widget. Send heartbeat every two seconds and playback
  report at least twice a second only while visible. Unsubscribe/clear timers/base64 data on teardown.
- [ ] Add registry options matching the spec, category Computer, icon in WIDGET_ICONS and import in main.js. During the
  mirror milestone, reject extend with an explicit unavailable state until Task 12; do not silently mirror when extend
  was requested. Default remains extend as specified. No default layout receives Screen. Missing bridge shows unsupported.
  Demo script provides real init/media fixtures, a selectable state simulator and fixed inputAvailable=false until Task 13.
  Build.py copies demo-only JS and Task 1 fixture bytes to the built site's fixture directory; never copy test TLS keys.
- [ ] Test page/stack/night/background/pause cleanup, empty bridge, denied/lost/busy/dependency states, old callbacks,
  control-off sends no input, and real MSE fixture playback. Add small/large widget screenshots. Compute actual registry
  count for README, initial GUIDE/SETUP consent/unsupported notes, locales and privacy language; run
  `python3 tools/check-release.py` and focused UI tests. Commit: `feat: add Screen mirror widget and demo`.

## Task 12: Enable extend mode, persistence and monitor reselection

**Files:** Modify ScreenFeeds.cs, ScreenEndpoint.cs, helper, screen.js, Screen*Tests.cs and screen.spec.js; extend GUIDE/SETUP.
**Interfaces:** Existing mode `extend` uses VIRTUAL and mode `mirror` uses MONITOR. Fixed operation
`{op:"chooseMonitor"}` applies only to the current mirror viewer. ScreenTokenStore.Clear invalidates that scope's lease.
No new public token fields or capture parameters.

- [ ] Add tests for default extend requesting only VIRTUAL, sequential restart restoring the same scope, control-mode
  separation, stale token replacement after chooseMonitor, no reselection in extend mode and no token in ToPublic/events.
  A direct token regression can be:
  ```csharp
  [Fact]
  public void ReselectionRejectsLatePermissionFromPreviousSession() {
      var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
      try {
          var store = new ScreenTokenStore(path);
          var scope = new ScreenScope("phone", "mirror", false);
          var consumed = store.Consume(scope);
          store.Clear(scope);
          Assert.False(store.Replace(consumed, "late-private-token"));
          Assert.Null(store.Consume(scope).Token);
      } finally { File.Delete(path); }
  }
  ```
- [ ] Run Screen token/feed/endpoint tests and focused screen UI tests; confirm the new extend/reselection cases fail first.
- [ ] Enable VIRTUAL in retained helper startup, with actual negotiated video caps. Wire Choose monitor again through the
  fixed native screenSend operation, cancel old session first, clear only the current mirror scope and start a fresh
  portal dialog. Accept unavailable persistence as new approval rather than restoring a different source silently.
- [ ] Verify repeated real extend start/close/reopen, removal after app/agent death, two phones and one busy second widget,
  agent restart with stored tokens, output arrangement behavior and ≤1080p mirror downscaling. Record results in the
  findings document without token values; update placement/consent docs to match observed KDE behavior.
- [ ] Rerun unit/UI checks; commit: `feat: extend the KDE desktop onto the paired phone`.

## Task 13: Add absolute pointer only for proven mappings

**Gate:** Execute only if Task 0 proves a usable logical mapping and input API for the shipping source/compositor.
Otherwise add the live-video/control-unavailable UI coverage and documentation, keep inputAvailable=false and omit pointer forwarding.
**Files:** Create screen-input.js, `tools/ui-tests/screen-input.spec.js`, `ScreenInputTests.cs`; modify helper,
ScreenFeeds.cs, screen.js and GUIDE. Extend Python tests for permission/mapping and releaseAll.
**Interfaces:**
```javascript
export function mapScreenPoint(rect, width, height, clientX, clientY) {}
// Return null outside contained content, otherwise {x,y} normalized to [0,1].
export function bindScreenInput(surface, { dimensions, enabled, send }) {}
// Return { releaseAll(), destroy() }; dimensions() returns {width,height}.
```
Agent-side `ScreenFeeds.InputAsync(connection, json, ct)` from Task 6 accepts only pointer/button/scroll/releaseAll messages. Helper maintains
pressed-button state and uses stream-local logical extents from the proven Task 0 mapping. Never substitute desktop
pixel positions or use ydotool. InputAvailable is true only after both permission and mapping validation.

- [ ] Add contained-video and gesture tests, starting with:
  ```javascript
  test('portrait letterbox bars cannot move the desktop pointer', async ({ page }) => {
    await page.goto('/panel/');
    const mapped = await page.evaluate(async () => {
      const { mapScreenPoint } = await import('/panel/widgets/screen-input.js');
      return mapScreenPoint({ left: 0, top: 0, width: 400, height: 800 },
        1920, 1080, 200, 20);
    });
    expect(mapped).toBeNull();
  });
  ```
  Cover edge clamping, scale-independent normalized coordinates, second finger cancelling tap/long-press, no duplicate
  left click after right click and touchcancel after button-down. Fake timer/DOM events must verify sent operations.
- [ ] Run `(cd tools/ui-tests && npx playwright test screen-input.spec.js)` and the Screen/Python input tests; observe red.
- [ ] Implement gesture recognition and enabled predicate requiring visible + control + inputAvailable + live epoch.
  Use pointer capture only while handling active gestures, prevent page swipes on the video surface, and releaseAll
  on every spec teardown/reset path. Absolute portal x/y must remain strictly below logical width/height at normalized 1.
  Buttons are an allow-list (left/right/middle); clamp scroll deltas to ±120 per message and rate-limit server-side.
- [ ] Recheck exact current widget consent for every server input, release on permission/config loss and cancel pending
  input on helper crash. Test an already-open socket after widget removal/control-off. Verify actual mirror/virtual
  scaled and rotated outputs on hardware; if any mapping fails, mark that source unavailable instead of guessing.
- [ ] Rerun targeted tests and update GUIDE support limits. Commit either `feat: add proven absolute screen touch control`
  or `docs: document display-only screen control limits`, reflecting the actual gate outcome.

## Task 14: Package optional dependencies and verify the release surface

**Files:** Modify setup.sh, tools/test-unix-installer.py, docs/SETUP.md, docs/GUIDE.md, README.md, PRIVACY.md if needed;
update findings with the final manual matrix. No CHANGELOG/version edits.
**Interfaces:** Setup uses the existing offer_extras / pkg_install / --no-extras / --dry-run behavior; capability probes
must fail without blocking normal agent startup. No unattended package installation is added.

- [ ] Add isolated shell-boundary tests to tools/test-unix-installer.py that fake package managers and capability output.
  Define a helper `run_screen_setup(package_manager, capabilities, flags)` that invokes a copied setup.sh with mocked
  commands in a temporary PATH and returns `stdout`, `stderr`, `returncode`, `install_calls`. Test:
  ```python
  def test_no_extras_never_installs_screen_dependencies(self):
      result = self.run_screen_setup("dnf", capabilities=[], flags=["--no-extras", "--dry-run"])
      self.assertEqual(result.returncode, 0, result.stderr)
      self.assertEqual(result.install_calls, [])
  ```
  Add unavailable optional encoder, unsupported distro and user-declined cases. Existing installer fixture conventions
  supply download/start boundaries; tests must never install packages or alter the user's home.
- [ ] Run `python3 tools/test-unix-installer.py`; confirm the new offer behavior test fails before editing setup.sh.
- [ ] Add Gio/Gst/PipeWire/encoder capability probes, verified distro package suggestions and opt-in offers on Linux only.
  Use the Task 0 dependency evidence, plus package-manager queries on the supported target distros. Distinguish unavailable
  third-party codec repositories from ordinary missing packages; show instructions without silently adding repositories.
  Unsupported distros receive manual instructions. No dependency is required to use the rest of PhoneBox.
- [ ] Run final automated checks from the repository root:
  ```bash
  dotnet test agent/PhoneBox.Agent.Tests
  python3 -m unittest discover -s agent/PhoneBox.Agent.Tests -p 'test_screen_helper.py' -v
  python3 tools/test-unix-installer.py
  python3 tools/test-bridge-checks.py
  python3 tools/check-release.py
  ```
  From `tools/ui-tests`: `npm ci`, `npx playwright install chromium`, `npx playwright test`.
  From `android`: `./gradlew testDebugUnitTest lintDebug assembleDebug` and `./gradlew connectedDebugAndroidTest`.
  Use normal dependency/download permission flow when required. Repeat only failed checks or checks affected by subsequent changes.
- [ ] Complete and record spec §5.9's hardware matrix: both routes, two phones, same-phone contention, HiDPI/rotation,
  output placement, app/agent/portal loss, token revocation and denial, ten-minute thermal/performance measurements and
  throttling recovery. Verify Linux release publish embeds the helper; macOS/Windows build smoke checks on their CI
  runners show unsupported with no capture helper startup. Test UI without native bridge and on the oldest available
  supported WebView. Document untested devices and blocked checks explicitly, never report them as passes.
- [ ] Check `git diff --check`, review changed-file scope and actual non-test PR size, and commit:
  `feat: package optional Linux screen dependencies and document support`.
  Request a focused review of auth/secret boundaries, stop/recovery races and the hardware evidence before merge.

## Coverage and handoff

| Spec requirement | Plan task(s) |
|---|---|
| G1/G2 virtual and physical source, consent/reselection | 0, 4, 11, 12 |
| G3 input permission, logical mapping and release | 0, 6, 7, 13 |
| G4 pairing/routes/native boundary | 7, 8, 9 |
| G5 encoder/MSE/latency/congestion | 0, 1, 5, 7, 8, 10, 14 |
| G6 ownership, token rotation, visibility, shutdown/retry | 3, 4, 5, 6, 7, 9, 11, 12 |
| G7 portability, failure states and API floor | 5, 9, 11, 14 |
| Packaging, registry/counts, docs and privacy | 11, 12, 14 |
| Unit/fake-helper/UI/instrumentation/manual verification | Every task; final matrix in 14 |

No product code is implemented by writing this plan. The next executable action is Task 0, followed by its evidence review.
Execution may be inline with executing-plans or delegated with subagent-driven-development only when the user selects it.
