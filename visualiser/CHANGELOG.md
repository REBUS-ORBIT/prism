# PRISM Visualiser changelog

The orchestrator versions independently of the PRISM Agent. The bump is
`Directory.Build.props::VisualiserVersion`; the CI tag convention is
`visualiser-v<VisualiserVersion>`.

## v0.5.10 — Headless-safe mesh spawn (object-spawn helper crashed UE)

> v0.5.9's discovery worked — the PC01 run logged `discovered 1 static mesh
> asset(s)` — but the moment there was a real mesh to place, UE crashed:
> `EXCEPTION_ACCESS_VIOLATION reading address 0x0000000000000040` in
> `UnrealEditor-EditorFramework.dll`, callstack through
> `PythonScriptPlugin → UnrealEd → EditorFramework`, then
> `FPlatformMisc::RequestExitWithStatus(1, 3)` — i.e. the `exit=3` the
> orchestrator surfaced as `ue_import_failed`. No `imported bounds` /
> `framing camera` / `PRISM_VISUALISER_READY` line was reached.

### Root cause

`_spawn_meshes_into_level()` placed each imported mesh with
`EditorActorSubsystem.spawn_actor_from_object(mesh, …)`. That object-spawn
helper fires **editor selection + component-visualizer notifications**
(EditorFramework) that dereference a null pointer under the headless
`PythonScriptCommandlet` (`-NullRHI`, Slate not initialised). It never
surfaced before because every prior run discovered **zero** meshes, so the
spawn loop body never executed. The lights spawn fine because they use the
**class-spawn** path (`spawn_actor_from_class`), which doesn't route through
that notification.

### Changed

- **`Unreal/PythonScripts/import_orbit.py(.in)`** — `_spawn_meshes_into_level()`
  no longer calls `spawn_actor_from_object`. It now spawns a plain
  `StaticMeshActor` via `_spawn_actor_from_class()` (the same proven path
  the Directional/Sky lights use) and assigns the imported mesh to the
  actor's `StaticMeshComponent` via `set_static_mesh()`. Each step is
  wrapped so a property-set failure degrades gracefully instead of taking
  down the commandlet. Bounds / framing / save / `READY` all run as before.

## v0.5.9 — Spawn the imported geometry (import_asset returns no assets)

> v0.5.8 lit and framed the level (Directional/Sky lights, framing camera,
> PlayerStart) and the PC01 run confirmed all of that landed — no
> `NO PLAYERSTART` warning, lights spawned, streamer reached Active. But
> the `PRISM_VISUALISER_READY` line reported **`assetCount: 0`** and the
> camera fell back to its origin framing, so the streamed scene was a
> lit-but-**empty** world: still no model.

### Root cause

`InterchangeManager.import_asset(content_path, source_data, params)` under
UE 5.7 returns a **results container** (or `None`), **not** the array of
created assets. `_normalise_imported_assets()` therefore yielded an empty
list even though Interchange logged `import completed` and wrote the
`StaticMesh` into the destination folder (the staged glTF had
`meshes=1 materials=1`). With zero meshes in hand, `_spawn_meshes_into_level()`
spawned **no** geometry actors — the level got lights + camera but no
model. This was pre-existing (the same `assetCount: 0` appears in the
v0.3.9 run logs); it was masked by the black frame until v0.5.8 lit the
scene.

### Changed

- **`Unreal/PythonScripts/import_orbit.py(.in)`** — when the import return
  value yields no `StaticMesh` (the normal UE 5.7 case), the driver now
  **discovers the assets Interchange actually wrote**:
  - `_scan_assets()` forces a synchronous `AssetRegistry.scan_paths_synchronous([TARGET_FOLDER], force_rescan=True)`
    so freshly-imported assets are registered before enumeration.
  - `_discover_static_meshes()` enumerates `TARGET_FOLDER` via
    `list_assets(recursive=True)`, loads each path, and keeps the
    `StaticMesh` instances. These are then spawned, bounds-computed, and
    framed exactly as before.
  - Logged as `discovered N static mesh asset(s) in <folder> after import`,
    so `assetCount` / `imported bounds … meshes=N actors=N` now reflect the
    real geometry instead of 0.
- Model-agnostic and additive: if a future UE build *does* return the asset
  array, that path is still preferred and the discovery fallback is skipped.

## v0.5.8 — Light + frame the imported scene so the stream isn't black

> The v0.3.9 PC01 run reached a fully healthy Pixel Streaming state —
> UE registered with Wilbur, the browser connected, WebRTC offer/answer
> completed, and both video+audio tracks went `State=[Active]` with a
> live data channel — but the admin player page showed a **solid black**
> viewport. Transport was never the problem; the *rendered scene* was
> black.

### Root cause

`import_orbit.py` built the streamed `Imported_<runId>` map with
`LevelEditorSubsystem.new_level()`, which creates a **completely blank**
UE level: no Directional Light, no Sky Light, no Sky Atmosphere, no
PostProcessVolume, no PlayerStart, and no camera. It then spawned the
imported static meshes at the world origin and saved. The Phase F
`-game` launch streams the default player camera, which spawns at world
origin with default orientation into an unlit void — so every streamed
frame is black even though the encoder, WebRTC tracks, and data channel
are all live. UE also logged `LogGameMode: FindPlayerStart: PATHS NOT
DEFINED or NO PLAYERSTART with positive rating` for the same reason
(no PlayerStart in the blank level). Two missing pieces — **lighting**
and **camera framing** — combined to produce the black frame.

### Changed

- **`Unreal/PythonScripts/import_orbit.py(.in)`** — after the Interchange
  glTF import the driver now makes the level actually viewable:
  - **Bounds** — `_compute_bounds()` unions the imported `StaticMesh`
    asset bounding boxes (CPU-side, reliable under `-NullRHI`; meshes are
    spawned at origin with identity transform so world bounds == local
    box). Falls back to `Actor.get_actor_bounds()` if an asset can't
    report a box. Logged as `imported bounds center=(…) radius=…`.
  - **Lighting** — `_add_lighting()` spawns a movable **Directional
    Light** (atmosphere sun, intensity 10), a **Sky Atmosphere**, and a
    movable **Sky Light** (real-time capture, intensity 1) — the UE
    "Basic" daylight set, which exposes correctly under default
    auto-exposure.
  - **Exposure safety net** — `_add_post_process()` spawns an unbound
    **PostProcessVolume** with auto-exposure clamped
    (`auto_exposure_min/max_brightness` 0.5–2.0, bias 1.0) so an
    empty/dark frame can't crush to black and a bright sky can't blow the
    model out. Override flags are set best-effort across naming
    conventions; the lit scene already guarantees a visible frame so this
    is belt-and-suspenders.
  - **Framing** — `_frame_view()` places a `CameraActor` at
    `center + normalize(1,-1,0.6) * radius * 2.5` looking at the centre
    (FOV 50, `auto_activate_for_player = PLAYER0`,
    `find_camera_component_when_view_target = True`) so the streamed
    `-game` view uses it, **and** a coincident `APlayerStart` at the same
    transform so the default pawn also spawns framed on the model and the
    `NO PLAYERSTART` warning disappears.
  - Everything is **model-agnostic**: framing is driven by the computed
    bounds, with a ~2 m fallback for degenerate/empty bounds. Each spawn
    is individually `try`/`except`-guarded so a UE-API drift on one actor
    can't abort the whole import; failures are logged on the `ue-editor`
    channel.

This is a Python-driver-only change; the Phase F streamer-connected path
(v0.5.7) is untouched.

## v0.5.7 — Recognise UE 5.7 / Wilbur "streamer registered" log shapes

> The v0.5.6 PC01 run got past the `signalling_not_found` hard-stop and
> the Wilbur signalling server bootstrapped + handshook with UE
> cleanly within ~23 s — but the orchestrator timed out at 120 s with
> `ue_game_start_timeout: UE did not register a streamer with Cirrus
> within 120s`. Root cause: the v0.3.8 `StreamerConnectedPattern`
> regex was inherited verbatim from the legacy Cirrus signalling
> server (`Streamer connected: orbit_<id>` / `streamer registered with
> id orbit_<id>`); UE 5.7 + Wilbur dropped that line entirely and now
> emit four completely different signals across two separate process
> stdouts. None of them matched the legacy regex, so the orchestrator
> never observed the registration.

### Root cause

PixelStreaming 2 (UE 5.5+) reorganised the signalling protocol around
EpicRtc. Every successful streamer registration emits at least these
four log lines:

1. **UE-side, canonical** — `LogPixelStreaming2EpicRtc:
   RoomSignallingContextObserver::OnJoined. Local participant joined
   the room. roomId=[orbit_<id>] localParticipantId=[orbit_<id>]
   state=[Joined]`
2. **UE-side, simpler form** — `LogPixelStreaming2RTC: Player
   (orbit_<id>) joined`
3. **Wilbur-side** — `info: > UnknownStreamer ::
   {"id":"orbit_<id>",...,"type":"endpointId"}` (UE introducing its
   id to Wilbur)
4. **Wilbur-side** — `info: < orbit_<id> ::
   {"type":"endpointIdConfirm","committedId":"orbit_<id>"}`
   (Wilbur acknowledging the streamer id)

Lines 1–2 only ever appear on UE's stdout; lines 3–4 only appear on
Wilbur's stdout. The previous orchestrator only watched Wilbur's
stdout and only matched the legacy `Streamer connected: ...` shape,
so it was blind to all four.

### Changed

- **`PixelStreaming/SignallingSupervisor.cs::StreamerConnectedPattern`**
  → replaced with `StreamerConnectedPatterns`, an ordered list of
  `NamedStreamerPattern(string Name, Regex Pattern)` records covering
  the four UE 5.7 / Wilbur shapes plus a `LegacyCirrus` fallback for
  graceful degradation on pre-PS2 environments. Order is canonical-
  first: `OnJoined` (with id), `OnJoined` (bare-signal fallback when
  `localParticipantId=[...]` absent), `PlayerJoined`,
  `EndpointIdConfirm`, `EndpointId`, then `LegacyCirrus`.
- **`PixelStreaming/SignallingSupervisor.cs::TryParseStreamerConnected`**
  now returns the `NamedStreamerPattern.Name` of whichever pattern
  fired, so the orchestrator can attribute the registration to a
  specific shape.
- **`PixelStreaming/SignallingSupervisor.cs::AwaitStreamerConnectedAsync`**
  returns a new `StreamerConnectedMatch(string StreamerId, string
  MatchedPattern)` record. The id is the empty string when the
  matched pattern doesn't carry one (e.g. the bare-signal `OnJoined`
  fallback).
- **`Unreal/UnrealLauncher.cs::LaunchGameMode` / `UnrealGameHandle`**
  — UE -game stdout / stderr is now copied into a per-handle
  `Channel<string>` exposed via `UnrealGameHandle.Lines` (mirrors
  `SignallingHandle.Lines`). Lines still flow to the existing
  `ue-game` Serilog channel; the new channel is purely additive so
  the Phase F watcher can match against UE-side log shapes that
  Wilbur never emits (`OnJoined` / `PlayerJoined`).
- **`Pipeline/VisualiserPipeline.cs::WaitForStreamerConnectedAsync`**
  merges Wilbur's `cirrusHandle.Lines` and the new
  `ueHandle.Lines` channels into one ordered async stream via
  `MergeChannelLines` (two-pump fan-in over an inner channel) and
  feeds the merged stream to
  `SignallingSupervisor.AwaitStreamerConnectedAsync`. The
  `StartStreamingAsync` log line additionally carries the matched
  pattern name + the captured streamer id (or `(none)`) for
  diagnostic parity with the new event surface.
- **`Pipeline/VisualiserPipeline.cs`** — emits a one-line diagnostic
  the moment the watcher fires:
  `phase-f: streamer registered (matched <pattern-name>)
  elapsed=<X.X>s`. Surfaces the elapsed time so a future regression
  in the matcher (taking 90 s to match a registration that completed
  in 5 s) is visible without a debugger.

### Tests

- **`tests/PRISM.Visualiser.Orchestrator.Tests/SignallingSupervisorTests.cs`**
  — replaced the legacy-Cirrus-only theory with a comprehensive theory
  covering all five named patterns (asserting both the captured id
  AND the matched-pattern name), plus a separate theory covering the
  bare-signal `OnJoined` fallback (empty id, canonical pattern name).
- New negative-cases theory rejects the pre-handshake Wilbur noise
  (`identify` / `config` / `ping` JSON, `New streamer connection`),
  malformed UE telemetry (`state=[Joining]` / Player joined without
  parens), and arbitrary log channels — guards against a regex-too-
  greedy regression.
- `AwaitStreamerConnectedAsync_FiresOnCanonicalOnJoined_FromUeStdout`
  pins the canonical pattern as the front-of-list winner when the
  exact PC01 v0.3.8 log shape is replayed.
- `AwaitStreamerConnectedAsync_FiresOnWilburEndpointIdConfirm`
  asserts graceful degradation: when only Wilbur-side lines are
  available, `EndpointId` (the first Wilbur signal in the captured
  flow) wins.
- `StreamerConnectedPatterns_OnJoinedIsFirst` pins ordering so a
  future refactor that accidentally moves the canonical pattern down
  the list fails CI.

### Compatibility

- Public API change: `TryParseStreamerConnected` gained an
  `out string matchedPattern` parameter and `AwaitStreamerConnectedAsync`
  now returns `Task<StreamerConnectedMatch>` instead of `Task<string>`.
  The matchers are static helpers consumed only by
  `VisualiserPipeline` and the tests, so there are no out-of-tree
  callers in this repo.
- The `LegacyCirrus` pattern preserves all previously-recognised
  shapes — environments still on the legacy Cirrus signalling server
  continue to work; the diagnostic line just reports
  `matched=LegacyCirrus`.

## v0.5.6 — Auto-bootstrap PixelStreaming2 / Wilbur on first run (UE 5.5+)

> **Closes [REBUS-ORBIT/prism#25](https://github.com/REBUS-ORBIT/prism/issues/25).**
>
> The v0.3.7 PC01 run passed Phase E (UE import) cleanly and hit
> Phase F with `signalling_not_found: Cirrus signalling script could
> not be located under 'C:\Program Files\Epic Games\UE_5.7\Engine\
> Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer'.
> Is the PixelStreaming2 plugin installed?`. The PixelStreaming2 C++
> plugin **was** installed — but the Node.js signalling server it
> needs is fetched on demand via `get_ps_servers.bat`, and that
> script hadn't been run yet. v0.5.6 makes the orchestrator do the
> bootstrap itself.

### Root cause

PS2 (UE 5.5+) split the signalling server out of the C++ plugin:

- **Stays with the engine launcher install (✅)**: the C++
  PixelStreaming2 plugin under `Engine\Plugins\Media\PixelStreaming2\`
  and its launch script `get_ps_servers.bat`.
- **Fetched on demand (✗ until v0.5.6)**: the `SignallingWebServer\`
  TypeScript sources (cloned by `get_ps_servers.bat` from
  `github.com/EpicGamesExt/PixelStreamingInfrastructure`), `node.exe`
  (downloaded by `start.bat`), and the compiled
  `SignallingWebServer\dist\index.js` (built by `npm run build`).

The previous orchestrator probed for `cirrus.js` / `Cirrus.js` /
`main.js` / `server.js` / `index.js` directly under
`SignallingWebServer\` — the *pre-5.5* layout — and surfaced
`signalling_not_found` when none existed. Even after running
`get_ps_servers.bat` manually the probe would still miss, because
PS2's signalling server is named "Wilbur" and lives at
`SignallingWebServer\dist\index.js`.

### Added

- **`PixelStreaming/SignallingBootstrap.cs`** — new first-run
  installer. `EnsureReadyAsync(UnrealInstall, …)`:
  1. probes for `dist\index.js` and short-circuits when present;
  2. runs `Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\get_ps_servers.bat /v 5.7`
     to download the EpicGamesExt PixelStreamingInfrastructure
     UE5.7 branch into the engine plugin tree;
  3. runs `SignallingWebServer\platform_scripts\cmd\start.bat
     --publicip 127.0.0.1 -- --player_port 65000 --streamer_port 65001
     --serve --console_messages verbose --log_config`, watches stdout
     for the first listening-line / "starting" log, then kills the
     entire process tree — the build artefacts (`dist/`, `node/`,
     `node_modules/`, `Common/dist`, `Signalling/dist`, …) survive
     the kill;
  4. writes a marker under
     `%LOCALAPPDATA%\PRISM.Visualiser\state\signalling_ready_<sha>.flag`
     keyed by the SHA of the UE root path so multiple parallel UE
     installs (e.g. 5.6 + 5.7) don't share a marker.
  Bootstrap stdout / stderr is forwarded to Serilog under the
  `ps-bootstrap` channel. Total budget: 8 minutes; the npm / tsc
  pass on a fresh disk typically takes 60-180 s. Throws
  `SignallingBootstrapException` (mapped to the new
  `signalling_bootstrap_failed` failed/v1 code) on partial-install /
  network failures.

### Changed

- **`Pipeline/VisualiserPipeline.cs::StartStreamingAsync`** — Phase F
  now calls `SignallingBootstrap.EnsureReadyAsync` before
  `SignallingSupervisor.Resolve`. On steady state this is a single
  `File.Exists` check (~µs).
- **`PixelStreaming/SignallingSupervisor.cs::Resolve`** — probes
  `SignallingWebServer\dist\index.js` first (Wilbur, UE 5.5+);
  falls back to the legacy top-level Cirrus candidates only if
  Wilbur isn't there. The new `IsWilbur` flag on
  `SignallingResolveResult` selects which CLI dialect
  `BuildStartInfo` emits. Probed paths are surfaced via
  `ProbedPaths` so a future `signalling_not_found` event can list
  the exact files inspected.
- **`PixelStreaming/SignallingSupervisor.cs::BuildStartInfo`** —
  emits wilbur's `commander`-style CLI:
  `node dist\index.js --player_port=N --streamer_port=M --serve
  --console_messages verbose --log_config`. Working directory is
  set to the wilbur package root (`SignallingWebServer\`) so
  wilbur's `config.json` + relative `http_root` paths resolve.
  Legacy Cirrus `--HttpPort=N` still emitted when `IsWilbur` is
  false (pre-5.5 plugin variants we don't formally support but
  might still encounter on customer workstations).
- **`PixelStreaming/SignallingSupervisor.cs`** — `StartAsync` now
  takes separate `playerPort` + `streamerPort` arguments. Ready-
  line + streamer-connected regexes extended to also match wilbur
  log shapes (`HTTP webserver listening on port N`,
  `Listening on :N`, `Streamer registered with id orbit_xxx`).
- **`PixelStreaming/SignallingSupervisor.cs::NodeExeRelative`** is
  joined by **`WilburNodeExeRelative`**: the resolver prefers the
  wilbur-bundled Node (`SignallingWebServer\platform_scripts\cmd\
  node\node.exe`) so the runtime version matches the one that
  built wilbur's `dist/`. Falls back to the legacy
  `Engine\Binaries\ThirdParty\Node\Win64\node.exe` only when the
  wilbur bundle isn't there.
- **`PixelStreaming/PixelStreamingSession.cs`** — exposes
  `StreamerPort` alongside `SignallingPort`. The player-facing
  `PlayerUrl` / `SignallingUrl` still derive from the player port,
  so the ready/v1 wire format is unchanged.
- **`Pipeline/VisualiserPipeline.cs`** — allocates two distinct
  TCP ports on the Wilbur path; UE's `-PixelStreamingURL` now
  points at the streamer port (was: the player port, which Wilbur
  doesn't accept streamer connections on).
- **`Models/FailedEvent.cs`** — adds
  `CodeSignallingBootstrapFailed = "signalling_bootstrap_failed"`.
  The `signalling_not_found` message string now lists the actual
  probed paths.

### Trade-off

The bootstrap pre-builds and momentarily starts wilbur on a pair of
loopback-only ports (65000 / 65001 by default), then kills the
process tree. There's a few-second window where a wilbur instance
is listening on those ports; if some other process on the
workstation happens to also try to bind 65000 or 65001 in that
window, the bootstrap doesn't conflict (kernel hands out ports
exclusive-by-default) but they'll see EADDRINUSE while wilbur is
alive. Documented inline; the chosen ports are deep in the
ephemeral range so collisions are exceedingly rare on a PRISM
workstation.

### Tests

- **`SignallingBootstrapTests`** — pure-function surface coverage:
  `IsReady` disk probe, marker path stability across UE-root casing
  variation, marker uniqueness across UE versions, `WilburReadyPattern`
  matches against five known shapes + three negatives, arg
  tokeniser handles quoted paths, hard-pinned canonical relative
  paths.
- **`SignallingSupervisorTests`** — adds three new cases:
  `Resolve_PrefersWilburEntrypoint_OverLegacyCirrusCandidates`,
  `Resolve_FallsBackToLegacyCirrus_WhenWilburMissing`, and
  `BuildStartInfo_Wilbur_EmitsCommanderStyleArgs` /
  `BuildStartInfo_LegacyCirrus_EmitsHttpPortArg` to pin the CLI
  dialect contract per flavour.

### Notes

- Closes [#25](https://github.com/REBUS-ORBIT/prism/issues/25).
- The orbit-ue-template's `.uproject` was already correctly
  declaring `"PixelStreaming2": { "Enabled": true }`
  (verified on `v0.1.0-ue5.7-scaffold`), so no template repo bump
  was needed.
- Failure-mode progression so far:
  `exit=-1` (no commandlet) → `exit=3` (Interchange/Slate gap) →
  `exit=0 + no marker` (parse miss) → `signalling_not_found`
  (wilbur not bootstrapped) → expected next is either
  `ready/v1` end-to-end or a downstream Phase F failure
  (UE -game registration, TURN handshake) that we'll document in
  a v0.5.7+ follow-up.

## v0.5.5 — Fix marker parser stripped by UE `[ts][ch]LogPython:` log prefix

> **Closes [REBUS-ORBIT/prism#23](https://github.com/REBUS-ORBIT/prism/issues/23).**
>
> v0.5.4 fixed the Interchange API drift, so the v0.3.6 PC01 import
> finally runs end-to-end inside `PythonScriptCommandlet` and Python
> emits `PRISM_VISUALISER_READY {...}` on stdout. v0.5.5 fixes the
> orchestrator side that was still misreading those emissions and
> returning `ue_import_failed: UE exited without a ready marker
> (exit=0)` despite a clean `exit=0` from the editor.

### Root cause

`UnrealLauncher` launches `UnrealEditor-Cmd.exe` with
`-stdout -FullStdOutLogOutput` (see `BuildStartInfoCore`), which
mirrors UE's full categorised log to stdout. Python `print(...)`
calls under `PythonScriptCommandlet` are captured by UE and
re-emitted with a `[YYYY.MM.DD-HH.mm.ss:fff][  N]<Channel>:` header.
The actual stdout line on PC01 v0.3.6 was:

```
[2026.05.28-12.13.40:178][  0]LogPython: PRISM_VISUALISER_READY {"runId": "20debf1c_...", "levelPath": "/Game/REBUS/Maps/Imported_20debf1c_...", "assetCount": 0, "importDurationMs": 277}
```

`ParseLine` / `ParseMvrLine` were column-zero anchored:

```csharp
if (line.StartsWith(ReadyMarkerPrefix, StringComparison.Ordinal))
```

so that branch never fired, the marker was silently dropped, and the
launcher fell through to the no-marker failure path on
`process.WaitForExitAsync` returning `exit=0`. The same parsing
bug affected all four prefixes:

- `ReadyMarkerPrefix` — `PRISM_VISUALISER_READY ` (Phase E)
- `ErrorMarkerPrefix` — `PRISM_VISUALISER_ERROR ` (Phase E)
- `MvrReadyMarkerPrefix` — `PRISM_VISUALISER_MVR_READY ` (Phase J)
- `MvrErrorMarkerPrefix` — `PRISM_VISUALISER_MVR_ERROR ` (Phase J)

Why this didn't surface earlier: every previous PC01 run failed
*before* `_emit_ready` ran (no commandlet → wrong python flag →
Interchange API drift → Slate assertion). v0.3.6 / v0.5.4 was the
first run that completes the script and emits the marker, exposing
the parse gap.

### Fixed

- **`Unreal/UnrealLauncher.cs::TryFindMarker`** (new) — small public
  helper that locates a marker prefix anywhere in a line via
  `IndexOf`, returning the trimmed JSON payload via an `out`
  parameter. Single implementation shared by both `ParseLine` (Phase
  E) and `ParseMvrLine` (Phase J), so the four marker prefixes are
  guaranteed to follow the same parsing contract.
- **`Unreal/UnrealLauncher.cs::ParseLine`** — now calls
  `TryFindMarker(line, ReadyMarkerPrefix, out var json)` /
  `TryFindMarker(line, ErrorMarkerPrefix, out var json)` instead of
  the column-zero `StartsWith` checks. Recognises the marker even
  when wrapped in `[ts][ch]LogPython:` log noise; recognises the
  bare `PRISM_VISUALISER_READY {...}` form unchanged (preserves
  backwards compatibility with any non-UE harness that prints the
  marker directly).
- **`Unreal/UnrealLauncher.cs::ParseMvrLine`** — symmetric fix for
  the Phase J markers using the same helper.

### Trade-off

A hostile downstream string that embedded the marker substring
mid-line could in principle spoof a marker. The scanner is only
attached to UE child-process stdout / stderr — never to anything
user-facing — and UE-side log lines never contain user-controlled
JSON outside our own `print`s, so the additional permissiveness is
safe for the commandlet contract. Documented inline on
`TryFindMarker` so future readers don't tighten the regex without
context.

### Tests

- **`tests/PRISM.Visualiser.Orchestrator.Tests/MvrGdtfDetectorTests.cs`**
  — extended with four new tests:
  - `ParseMvrLine_Recognises_Markers_When_Prefixed_By_UE_Log_Header`
    — exact `[ts][  0]LogPython:` shape on both ready / error MVR
    markers.
  - `ParseLine_Recognises_Ready_And_Error_Markers` — covers the
    bare column-zero form for the Phase E markers (was previously
    only tested for the MVR variants).
  - `ParseLine_Recognises_Markers_When_Prefixed_By_UE_Log_Header`
    — uses the verbatim line shape captured from the v0.3.6 PC01
    run that triggered #23.
  - `TryFindMarker_Returns_True_With_Trimmed_Payload_When_Prefix_Present`
    + `TryFindMarker_Returns_False_When_Prefix_Absent` — direct
    unit tests on the new helper, including the trailing-whitespace
    trim contract.

All 95 orchestrator tests pass on `dotnet test
PRISM.Visualiser.sln -c Release`.

### Notes

- No `BuildStartInfoCore` / `BuildMvrStartInfoCore` changes — the
  `-stdout -FullStdOutLogOutput` flags stay; the parser learns to
  cope with the prefix UE has been adding all along. Removing the
  flags would lose error-path log fidelity (the same flags are what
  give us a usable failure-diagnostic stream when UE asserts before
  the python emits a marker).
- Out of scope: Phase F bring-up. Once #23 lands, the orchestrator
  should proceed to Cirrus + UE `-game` launch and either succeed
  end-to-end or surface its own `signalling_*` / `ue_game_*` failure
  code.

## v0.5.4 — Fix UE 5.7 Interchange API drift + drop Slate-bound AssetImportTask fallback

> **Fixes the Phase E UE import on PC01 (and any other UE 5.7
> workstation) failing with `ue_import_failed: UE exited without a
> ready marker (exit=3)`. With v0.5.3, `import_orbit.py` finally
> starts inside `PythonScriptCommandlet`; v0.5.4 fixes the very next
> bug the script hits — two cascading UE-API regressions previously
> masked because the commandlet never started.**

### Root cause

#### Bug 1 — `InterchangeManager.get_interchange_manager()` removed in 5.7

`_import_via_interchange` in `import_orbit.py.in` called
`unreal.InterchangeManager.get_interchange_manager()`, which was the
pre-5.5 name for the scripted singleton accessor. UE 5.5 renamed it
to `get_interchange_manager_scripted()` and dropped the old name; on
5.7 the call surfaces as:

```text
LogPython: Warning: Interchange import failed; falling back to
  AssetImportTask: type object 'InterchangeManager' has no attribute
  'get_interchange_manager'
```

The first-line warning then drove execution into the
`AssetImportTask` fallback branch (Bug 2).

#### Bug 2 — `AssetImportTask` is Slate-bound, crashes under `-NullRHI`

The `_import_via_asset_task` fallback used
`unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])`.
Even with `task.set_editor_property("automated", True)`,
`AssetImportTask` internally routes through Slate (the
import-settings dialog still constructs Slate widgets so the
factory can read the user's last choices). Slate is NOT initialised
when UE runs as `PythonScriptCommandlet` with `-NullRHI` — the very
next line in the per-run `REBUSVis.log` is the assertion:

```text
LogWindows: Error: appError called: Assertion failed:
  CurrentApplication.IsValid()
  [File:Slate/Public/Framework/Application/SlateApplication.h]
  [Line: 321]
```

UE then `RequestExit(1, 3, ...)`'s out, the orchestrator's
`UnrealLauncher.SafeExitCode` reports `3`, and the failure surfaces
as `ue_import_failed: UE exited without a ready marker (exit=3)`.

#### Bug 3 (latent) — `ImportAssetParameters.destination_path`

`_build_import_parameters` set
`params.destination_path = target_folder`. That field doesn't exist
on UE 5.5+'s `ImportAssetParameters` (`reimport_asset`,
`reimport_source_index`, `is_automated`, `follow_redirectors`,
`override_pipelines`, `import_level`, `destination_name`,
`replace_existing`, plus the `on_*_done` callbacks — that's the full
shape). Setting an unknown attribute on an `unreal.Object` subclass
raises `AttributeError` on 5.7. Even if Bugs 1 + 2 had been fixed,
the parameter object would have failed to construct.

The destination content path is conveyed as the first positional
argument to `InterchangeManager.import_asset(...)`, NOT as a field
on the parameters object.

### Fixed

- **`Unreal/PythonScripts/import_orbit.py.in::_get_interchange_manager`**
  (new) — try `get_interchange_manager_scripted` first; fall back to
  the legacy `get_interchange_manager` name on older 5.x point
  releases for diagnostic value; raise a `RuntimeError` with a
  user-actionable message if neither attribute exists.
- **`Unreal/PythonScripts/import_orbit.py.in::_import_via_interchange`**
  — built `source_data` explicitly via
  `unreal.InterchangeManager.create_source_data(gltf_path)`, called
  `manager.import_asset(target_folder, source_data, params)` with
  the canonical 5.5+ signature, and removed the speculative
  `import_asset_with_params` branch (that method was never on the
  binding).
- **`Unreal/PythonScripts/import_orbit.py.in::_build_import_parameters`**
  — removed the bogus `destination_path` attribute set; kept
  `is_automated`, `replace_existing`, added `follow_redirectors`.
  The destination content path is now exclusively the first
  positional argument to `import_asset`.
- **`Unreal/PythonScripts/import_orbit.py.in::main`** — dropped the
  `try/except` that wrapped `_import_via_interchange` and the
  `_import_via_asset_task` fallback it called. Removed the entire
  `_import_via_asset_task` function. Interchange is now required;
  any Interchange failure bubbles up to the outer try/except, which
  already emits `PRISM_VISUALISER_ERROR ... import_failed` and
  `sys.exit(1)` — the canonical structured failure surface for the
  orchestrator. The orchestrator's `UnrealLauncher.WaitForReadyMarker`
  treats this as `ue_import_failed` with exit code 6.
- **`Unreal/PythonScripts/import_orbit.py`** — lintable twin updated
  in lock-step with the template so the body matches exactly.

### Not in scope

- **`prism-visualiser.exe` is unsigned** (no Authenticode signature
  on the framework-dependent publish), so first-run SmartScreen /
  Defender may show a one-time warning on a fresh workstation. This
  is the same posture as v0.5.3 — separate ticket if a UAC prompt
  recurs across sessions.
- **MVR / GDTF Phase J import script (`import_mvr.py.in`)** —
  intentionally untouched. The MVR DMX-plugin path uses
  `AssetImportTask` (DMX factory class) but is invoked under a
  separate UE pass for which the `-NullRHI` constraint may need to
  be re-evaluated as part of a future Phase J PR. Out of scope for
  issue #21.

### Tests

- No new unit tests — the fix lives entirely in the Python script
  and exercises UE-side behaviour that requires a real UE 5.7
  install. Existing C# orchestrator + scaffolder tests
  (`UnrealEnvironmentTests`, `MvrGdtfDetectorTests`,
  `ProjectScaffolderTests`) all pass unchanged: the
  `ProjectScaffolder` template-rendering contract
  (`{{RUN_ID}}` / `{{GLTF_PATH}}` / `{{TARGET_FOLDER}}` /
  `{{LEVEL_NAME}}`) is unchanged in this PR.
- End-to-end verification: dispatch a visualiser stream against PC01
  after installing the v0.3.6 agent MSI — the per-run
  `REBUSVis.log` should reach `PRISM_VISUALISER_READY` instead of
  stopping at `Assertion failed: CurrentApplication.IsValid()`.

## v0.5.3 — Fix `ue_import_failed: UE exited without a ready marker (exit=-1)`

> **Fixes the Phase E UE import on PC01 (and any other UE 5.7 workstation):
> `UnrealEditor-Cmd.exe` was being launched with the wrong python-script
> flag for commandlet mode, so it loaded fully, refused to run
> `import_orbit.py`, and exited with code `-1` before the orchestrator
> could see a ready marker.**

### Root cause

`UnrealLauncher.BuildStartInfoCore` (and the matching MVR variant
`BuildMvrStartInfoCore`) passed `-ExecutePythonScript=<path>` alongside
`-run=PythonScript`. That combination drives UE into the
`PythonScriptCommandlet` code path, which only honours `-Script=<path>`
— it explicitly rejects `-ExecutePythonScript`. The PC01 `REBUSVis.log`
under `%LOCALAPPDATA%\PRISM.Visualiser\runs\<runId>\REBUSVis\Saved\Logs\`
shows the diagnostic in plain text every time:

```text
LogEditorPythonExecuter: Error: -ExecutePythonScript cannot be used by a
  commandlet. Use -run=PythonScript instead?
LogPythonScriptCommandlet: Error: -Script argument not specified
LogCore: Engine exit requested (reason: Commandlet
  PythonScriptCommandlet_0 finished execution (result -1))
```

UE returned exit code `-1` (the commandlet error code), surfaced
verbatim by `UnrealLauncher.SafeExitCode`, and bubbled up as
`ue_import_failed: UE exited without a ready marker (exit=-1)`.
`import_orbit.py` (the Phase E import driver) was therefore never
executed.

### Fixed

- **`Unreal/UnrealLauncher.cs::BuildStartInfoCore`** — replace
  `-ExecutePythonScript=<path>` with `-script=<path>`. Matching XML doc
  comments updated to the canonical UE 5.7 commandlet form
  (`UnrealEditor-Cmd.exe <project> -run=PythonScript -script=<py>`).
- **`Unreal/UnrealLauncher.cs::BuildMvrStartInfoCore`** — same fix for
  the Phase J MVR/GDTF second pass; both UE invocations were broken
  identically.
- **`Unreal/PythonScripts/import_orbit.py` / `import_orbit.py.in`** —
  doc-comment header updated so artists who read the rendered script
  see the actual command line the orchestrator now uses.

### Not in scope

- **`prism-visualiser.exe` is unsigned** (no Authenticode signature on
  the framework-dependent publish), so first-run SmartScreen / Defender
  may show a warning on a fresh workstation. This is a one-time
  interactive prompt — not the UAC elevation prompt — and it does NOT
  block subsequent launches once the operator accepts. The agent
  scheduled task on PC01 (`RunLevel=Highest`, `LogonType=Interactive`)
  was verified to spawn the orchestrator without UAC on this release:
  the process token check (`OpenProcessToken` + `TokenElevation`)
  returns `elevated=False`, so no consent UI is triggered. If the
  workstation operator reports a recurring UAC prompt on a future
  workstation, audit-sign the orchestrator + agent payload (separate
  ticket).

### Tests

- No new unit tests — the wrong-arg failure is a UE-side behaviour that
  needs a real UE install to exercise. The existing
  `MvrGdtfDetectorTests` keep using `UnrealLauncher.RenderMvrTemplate`
  and `UnrealLauncher.ParseMvrLine`, which are unaffected.
- End-to-end verification: dispatch a visualiser stream against PC01
  after installing the v0.5.3 orchestrator zip — `REBUSVis.log` should
  reach `PRISM_VISUALISER_READY` instead of stopping at
  `LogPythonScriptCommandlet: Error: -Script argument not specified`.

## v0.5.2 — UE pre-flight diagnostics + path normalization

Surfaced the `ue_root_not_found` failure mode that blocked PC01's first
end-to-end live run. Two layered fixes; either alone would have been
enough to mask the other for another release.

### Path normalization (`Unreal/UnrealEnvironment.cs`)

- `NormalizeRoot(string)` strips leading/trailing whitespace, BOMs
  (`\uFEFF`), zero-width spaces/joiners (`\u200B`–`\u200D`), and
  trailing directory separators before resolving the value via
  `Path.GetFullPath`. The canonical form is what every subsequent
  `Directory.Exists` / `File.Exists` check sees. Interior whitespace
  is preserved — `C:\Program Files\Epic Games\UE_5.7` is a legal
  Windows path and a blanket whitespace filter would mangle it. The
  `Path.GetFullPath` call also collapses mixed separators
  (`C:/Foo\Bar` → `C:\Foo\Bar`) and is wrapped in try/catch for
  illegal-character inputs so a malformed config never crashes the
  pre-flight.
- `ProbeFromRoot(root, source, probe)` is the new per-probe core. It
  normalizes, then validates the directory + `Engine\Binaries\Win64\
  UnrealEditor-Cmd.exe` separately, returning an `UnrealProbeOutcome`
  that records the source, raw / normalized roots, directory + editor
  existence flags, expected editor path, and a failure reason string
  shaped for direct inclusion in operator logs.
- `ResolveDetailed(probe)` is the new public diagnostic API that
  returns `UnrealResolution(Install?, IReadOnlyList<UnrealProbeOutcome>)`.
  The legacy `TryResolve` stays as a thin `=> ResolveDetailed(probe).Install`
  wrapper so existing call sites are untouched.

### Diagnostic message folding (`Program.cs`)

- `RunPhaseFAsync` calls `ResolveDetailed` and logs every probe outcome:
  Information for the match, Warning for each miss with raw +
  normalized + reason. On failure the `failed/v1` stdout event message
  is built by `FormatUeRootFailure(resolution)`, which folds every
  probe's reason into a single line separated by ` | `. Example output:

      ue_root_not_found: UNREAL_ENGINE_ROOT is set but does not point
      at a valid UE 5.7 install. | [EnvironmentVariable] raw=C:\Wrong
      normalized=C:\Wrong — directory does not exist: C:\Wrong |
      [DefaultPath] path=C:\Program Files\Epic Games\UE_5.7 —
      directory does not exist: C:\Program Files\Epic Games\UE_5.7 |
      [Registry] path=<unset> — HKLM\SOFTWARE\EpicGames\Unreal
      Engine\5.7\InstalledDirectory not present

### Tests

- `UnrealEnvironmentTests` adds 9 cases: trailing-backslash, leading
  BOM, mixed separators, populated diagnostics on failure, partial
  install (dir exists but editor missing), and four `NormalizeRoot`
  edge cases (empty, whitespace-only, invisible-only, interior
  whitespace preservation). Full orchestrator suite now 90 tests, all
  green.

## v0.5.0 — Phase J: MVR/GDTF detection + import

End state: the orchestrator detects MVR (My Virtual Rig) scene files and
GDTF (General Device Type Format) fixtures in either the staged ORBIT
scene or the per-run `attachments/` directory, and runs a SECOND UE
editor pass to import them via the DMX plugin before the streaming-ready
event fires. Mesh-only scenes are unchanged — the new path is fully
opt-in based on detector output.

### MVR/GDTF detector (`Unreal/MvrGdtfDetector.cs`)

- `MvrGdtfDetector.Detect(scene, runStageDir)` walks the staged scene
  tree for any node whose `SpeckleType` matches one of `MvrGdtfTypes`
  (`Orbit.Objects.Lighting.MvrScene`, `Orbit.Objects.Lighting.GdtfFixture`),
  AND enumerates `{runStageDir}/attachments/*.mvr` / `*.gdtf` on disk.
  Returns a deduplicated `MvrGdtfPaths` record with `HasAny` shortcut.
- Speckle objects are surfaced today via Phase C's `FallbackConverter`
  as `StagedUnknown` records — the detector parses the preserved
  `RawJson` looking for a `displayValue` / `blobPath` / `filePath`
  string. A future Phase-J converter that emits typed
  `StagedMvr` / `StagedGdtf` subtypes can short-circuit this branch.
- Path extraction is tolerant: unknown body shape just means "no path
  for this node" — already-logged Phase C fallback warning is enough.

### import_mvr.py.in (`Unreal/PythonScripts/`)

- Mirrors the `import_orbit.py.in` placeholder-template + lintable-twin
  layout. Placeholders: `{{RUN_ID}}`, `{{MVR_PATHS_JSON}}`,
  `{{GDTF_PATHS_JSON}}`, `{{TARGET_FOLDER}}`, `{{LEVEL_NAME}}`.
- GDTF fixtures are imported FIRST via the `AssetImportTask` + DMX
  plugin's GDTF import factory (class name varies across UE 5.x point
  releases — `DMXImportGDTFFactory` / `DMXGDTFImportFactory` /
  `UDMXGDTFImportFactory`; the script tolerates all three via
  `getattr` chains). MVR scenes import second so they can resolve
  their fixture references.
- MVR import tries `DMXImportMVRFactory.import_mvr_to_world(world, path)`
  first; falls back to spawning a `DMXMVRSceneActor` and calling its
  `import_mvr_archive(file_path)` instance method on older builds.
- Emits `PRISM_VISUALISER_MVR_READY { runId, gdtfCount, mvrCount,
  importDurationMs }` on success and `PRISM_VISUALISER_MVR_ERROR
  { code, message }` + `sys.exit(1)` on failure. Per-file errors are
  warned-and-continued rather than aborting the whole pass.
- The artist who lands `v1.0.0-ue5.7` MUST validate these calls against
  the installed UE version's DMX plugin surface — the header comment
  calls this out explicitly.

### UnrealLauncher.LaunchMvrImportAsync (`Unreal/UnrealLauncher.cs`)

- New method mirroring `LaunchImportAsync`'s shape but for the MVR pass.
  Renders the template by replacing `{{MVR_PATHS_JSON}}` /
  `{{GDTF_PATHS_JSON}}` with JSON-encoded path arrays so the rendered
  script can `json.loads` them safely regardless of backslash density.
- New marker prefixes `PRISM_VISUALISER_MVR_READY` /
  `PRISM_VISUALISER_MVR_ERROR` with their own JSON parsers
  (`UnrealMvrReadyMarker` / `UnrealMvrErrorMarker`) and source-gen
  contexts to stay trim+AOT friendly. Same TaskCompletionSource +
  WhenAny race pattern as `LaunchImportAsync`.
- Same default `10 min` timeout, same JobObject `KILL_ON_JOB_CLOSE`
  participation, same UE stdout forwarding to the `ue-editor` Serilog
  channel.

### Pipeline wiring (`Pipeline/VisualiserPipeline.cs`)

- `ImportAsync` now optionally takes the `StagedScene` + `runStageDir`
  so it can run the detector + (conditionally) the second UE pass
  inline. If `MvrGdtfPaths.HasAny == false` the method returns
  exactly as Phase E did — no behavioural regression for mesh-only
  scenes.
- An MVR import failure (python error, no marker, timeout) does NOT
  abort the run: the glTF geometry is already imported and the level
  is streaming-eligible. The failure is logged + surfaced on the
  returned `ImportResult.MvrImport` for the agent to forward to the
  server's progress channel.
- `StageOutcome` grows `StagePath` + `StagedScene` so the CLI doesn't
  need to re-walk anything between Phase C and Phase E/J.

### Tests

4 new xUnit `[Fact]`s in `MvrGdtfDetectorTests`:

1. **Detector finds Speckle MVR object.** Synthetic StagedScene with
   one Collection containing a `StagedUnknown` of
   `SpeckleType = "Orbit.Objects.Lighting.MvrScene"` whose
   `RawJson` carries a `displayValue` pointing at a fake staged file.
2. **Detector finds attached MVR file by extension.** Empty
   StagedScene, `stage/{runId}/attachments/lighting.mvr` exists.
3. **Detector returns empty when none present.** Mesh-only scene,
   no attachments dir.
4. **Detector handles both sources at once.** Speckle MVR object +
   filesystem GDTF, both returned and deduplicated.

End-to-end (real MVR file → UE DMX plugin → MVR actor in level)
gates on the `v1.0.0-ue5.7` template + a workstation with the DMX
plugin enabled. Out of scope for this release.

## v0.4.0 — Phase F: Pixel Streaming 2 bring-up on localhost

End state: the orchestrator no longer exits with code `9` after the
import — it brings Cirrus + UE up under PixelStreaming 2 on localhost,
emits the final `prism-visualiser/ready/v1` JSON line on stdout with
real local URLs, and blocks until UE exits or external cancellation
(Ctrl+C / SIGTERM). The end-to-end "browser sees the stream" check
still gates on (a) a workstation with UE 5.7 installed, (b) Phase D's
`v1.0.0-ue5.7` artist template, and (c) a GPU with hardware NVENC.

### PixelStreaming components (`PixelStreaming/`, BUILD.md §4)

- `PortAllocator` — TCP / UDP port allocation via the
  `IPAddress.Loopback:0` bind-and-release trick. Includes a
  "N distinct ports in one shot" helper that binds all sockets in
  parallel for guaranteed uniqueness (the per-call variant can race
  the OS into handing out the same port twice on a tight loop).
- `SignallingSupervisor` — locates Cirrus under
  `<UE_ROOT>\Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\`
  and Node at
  `<UE_ROOT>\Engine\Binaries\ThirdParty\Node\Win64\node.exe`. Spawns
  Cirrus via `ProcessSupervisor` and parses the ready line +
  "streamer connected" line via permissive regexes that match the
  three known PS2 log shapes (UE 5.5 / 5.6 / 5.7). Both script and
  Node binary paths can be overridden for local smoke testing via
  `PRISM_VISUALISER_CIRRUS_SCRIPT` / `PRISM_VISUALISER_NODE_EXE` env
  vars.
- `PixelStreamingSession` — composes `UnrealGameHandle` +
  `SignallingHandle`, exposes `RunUntilExitAsync` (block until UE
  exits or cancellation), and `ShutdownAsync` (kill UE FIRST, then
  Cirrus, with a `5s` grace period before the JobObject
  KILL_ON_JOB_CLOSE backstop catches anything stuck — UE-first
  ordering keeps the shutdown log clean of WebRTC peer-disconnect
  spam).

### UnrealLauncher — game-mode launch (BUILD.md §4)

`UnrealLauncher.LaunchGameMode` adds the `-game` invocation alongside
the existing import path. We keep `UnrealEditor-Cmd.exe` (NOT
`UnrealEditor.exe`) because both binaries share the engine monolith
but `-Cmd` links against the Win32 Console subsystem, so its stdout
/ stderr are inherited cleanly by us; the GUI-subsystem variant's
`AddLogListener(stdout)` path is unreliable when launched from a
non-console parent. `-game` mode is fully supported by `-Cmd` (the
switch picks the `UGameEngine` path inside Unreal regardless of
which subsystem the binary linked against). PS2 still creates a real
D3D12 device + NVENC encoder under `-RenderOffScreen`; we
intentionally do NOT pass `-NullRHI` for the game-mode launch (it
would disable the very RHI that drives the streamer). Full argument
list:

```
UnrealEditor-Cmd.exe <project>.uproject /Game/REBUS/Maps/Imported_<runId> -game \
  -RenderOffScreen -ResX=1920 -ResY=1080 \
  -PixelStreamingURL=ws://127.0.0.1:<signallingPort> \
  -PixelStreamingID=orbit_<short> \
  -Unattended -NoSplash -NoPause -stdout -FullStdOutLogOutput -log
```

PS2 (UE 5.5+) deprecated `-PixelStreamingIP` / `-PixelStreamingPort`
in favour of `-PixelStreamingURL`; the plan §Risks calls this out
and the launcher uses only the new form.

### Pipeline + CLI

- `VisualiserPipeline.StartStreamingAsync` — Phase F continuation:
  resolve Cirrus + Node, allocate ports, spawn Cirrus, wait for
  ready, spawn UE -game, wait for streamer-connected. Returns a
  live `PixelStreamingSession` the caller emits the ready event
  against. Exception ladder maps each typed failure (Cirrus
  missing, Node missing, signalling timeout, streamer-connected
  timeout, UE crashed before streamer connected) to a
  matching `prism-visualiser/failed/v1` event + exit code.
- `Program.RunPhaseFAsync` — replaces the old `RunPhaseEAsync`'s
  `exit 9` after `imported/v1`. The orchestrator now goes all the
  way through to either a real streaming-ready state (emit
  `ready/v1`, block until UE exits / Ctrl+C, exit 0) or one of the
  Phase F failure paths (7, 8). `Console.CancelKeyPress` +
  `AppDomain.ProcessExit` are wired to flush stdout before the CLR
  tears the process down.
- New exit codes: `7` (signalling failed to start within `30s`),
  `8` (UE -game failed to launch or never registered a streamer
  within `120s`, OR UE exited non-zero after the streamer had
  connected). Old `9` is reserved (was the Phase F not-implemented
  sentinel through v0.3.0; no longer emitted).
- New `failed/v1` codes: `signalling_not_found`, `node_not_found`,
  `signalling_start_timeout`, `ue_game_start_timeout`,
  `ue_game_crashed`.

### Tests

3 new xUnit `[Fact]` classes (covers Phase F smoke tests 11, 13, 15):

- `PortAllocatorTests` — allocates 5 distinct ephemeral TCP ports
  in a tight loop and asserts each is bindable; mirrors the same
  contract for UDP; covers the hint-honouring helper.
- `SignallingSupervisorTests` — drives the ready-line + streamer
  -connected parsers against the three known PS2 log shapes and
  noisy fixtures; exercises `AwaitReadyAsync` via a synthetic
  `IAsyncEnumerable<string>` so no real Cirrus process is needed;
  covers env-var overrides for smoke testing.
- `PixelStreamingSessionShutdownTests` — verifies the static
  `PixelStreamingSession.ShutdownAsync` cleanup helper kills UE
  before Cirrus, waits for each to exit before moving to the next,
  is no-op-safe when UE has already exited, and tolerates a
  hanging Cirrus by respecting the configured grace period.

End-to-end (browser-sees-stream) coverage stays out of scope until
the artist-populated `v1.0.0-ue5.7` template lands and a
UE-installed workstation is available.

## v0.3.0 — Phase E: UE Python import + editor scaffold

End state: the orchestrator opens a fully imported UE project but does
not yet stream pixels. Pixel Streaming bring-up is Phase F. The `stream`
subcommand (without `--dry-run`) now layers a UE editor invocation on
top of the Phase C glTF stage, then exits with code `9` (NotImplemented)
once the imported level is on disk.

### UE environment + template management (BUILD.md §3.1)

- `UnrealEnvironment` resolves a UE 5.7 install in priority order:
  `UNREAL_ENGINE_ROOT` env var → default
  `C:\Program Files\Epic Games\UE_5.7\` → registry
  `HKLM\SOFTWARE\EpicGames\Unreal Engine\5.7\InstalledDirectory`.
  Validates `Engine\Binaries\Win64\UnrealEditor-Cmd.exe` is present; an
  env var that points at a missing root surfaces `EnvVarSet=true` so the
  CLI can emit `code: "ue_root_not_found"` and exit 4.
- `TemplateFetcher` downloads
  `REBUS-ORBIT/orbit-ue-template`'s release zip, content-addressed by
  tag under `%LOCALAPPDATA%\PRISM.Visualiser\ue-template\<tag>\`.
  SHA256 sidecar + verify on cache hit; `IZipDownloader` abstracts the
  HTTP layer for unit tests. Default tag is the Phase D scaffold
  (`v0.1.0-ue5.7-scaffold`); the artist-populated `v1.0.0-ue5.7` tag
  is a future milestone.
- `ProjectScaffolder` per-run clones the cached template to
  `%LOCALAPPDATA%\PRISM.Visualiser\runs\<runId>\REBUSVis\`, hoists the
  zip's top-level folder if any, rewrites `REBUSVis.uproject`'s
  `Description`, replaces (or appends)
  `[/Script/EngineSettings.GameMapsSettings]::GameDefaultMap` to
  `/Game/REBUS/Maps/Imported_<runId>.Imported_<runId>`, and renders
  the per-run `import_orbit.py` from `import_orbit.py.in`.
- `UnrealLauncher` spawns `UnrealEditor-Cmd.exe -run=PythonScript`
  with the rendered script, the project flag, and headless flags
  (`-unattended -nullrhi -nosplash -nopause -log`). stdout / stderr
  are line-buffered through `ProcessSupervisor`; the launcher
  greps for `PRISM_VISUALISER_READY {…}` / `PRISM_VISUALISER_ERROR {…}`
  markers, enforces the 600s timeout, and surfaces a structured
  `ImportResult`.

### Python import script (BUILD.md §3.2)

- `import_orbit.py.in` drives UE 5.7's Interchange framework directly
  (`unreal.InterchangeManager` + `ImportAssetParameters`). Falls back
  to `unreal.AssetImportTask` if the Interchange API surface differs
  on the installed engine; tolerates `EditorAssetLibrary` →
  `EditorAssetSubsystem` deprecation via `getattr` lookups. Idempotent
  per-run, emits the `PRISM_VISUALISER_READY` JSON line on success,
  and `PRISM_VISUALISER_ERROR` + `sys.exit(1)` on failure.
- `import_orbit.py` is a literal-placeholder twin used only so the
  template can be Python-lint-checked outside the UE editor. The
  scaffolder always renders from `import_orbit.py.in`.

### Pipeline orchestration (BUILD.md §3.3)

- `VisualiserPipeline` wraps Phase C's
  `OrbitReceivePipeline.ReceiveAsync` + `SceneFlattener.Flatten` +
  `GltfWriter.Write` and the new `UnrealLauncher.LaunchImportAsync`,
  returning an `ImportResult` (project path, level path, asset count,
  import duration). This is the surface Phase F will wrap with Pixel
  Streaming.

### CLI

- `stream` (without `--dry-run`) now emits, in order:
  `prism-visualiser/staged/v1` (Phase C, unchanged) → after a
  successful UE import, `prism-visualiser/imported/v1` with the
  resolved project path / level path / `assetCount` /
  `importDurationMs` → exits `9` (NotImplemented) until Phase F lands.
- New failure event `prism-visualiser/failed/v1` reports phase /
  code / message. New exit codes: `4` (UE root not found),
  `5` (UE import timed out), `6` (UE import failed — non-zero
  editor exit or python error marker on stdout). The dry-run path
  and exit codes `0` / `1` / `9` / `64` are unchanged.

### Tests

13 new xUnit `[Fact]`s across three classes (UnrealEnvironment
resolution, TemplateFetcher cache hit / miss / tampering,
ProjectScaffolder zip-extract + uproject / ini rewrite + python
render — including ini round-trip parse and nested-zip flattening).
UE-dependent end-to-end coverage stays out of scope until the
artist-populated `v1.0.0-ue5.7` template lands and a UE-installed
workstation is available; this is documented in each new test
class's XML doc.

## v0.2.0 — Phase C: ORBIT receive pipeline + glTF staging

First substantive build on top of the Phase B scaffold. The `stream`
subcommand (without `--dry-run`) now does real work: authenticates,
fetches the version's full object tree, stages it as a glTF + sidecar
manifest, then exits with code `9` (NotImplemented) until Phase D/E
land the UE bring-up.

### Receive pipeline (BUILD.md §1)

- Composite `IOrbitTokenSource`: env vars (`ORBIT_PAT_PROD` /
  `ORBIT_PAT_DEV`) → `%LOCALAPPDATA%\PRISM.Visualiser\auth\<server>.json`
  → fail-with-OrbitAuthException. Mirrors the Rhino-connector token
  store schema (`prism-visualiser/auth/v1`).
- `HttpOrbitApi` over `HttpClient` + Polly: bearer auth, 3-attempt
  exponential back-off on 408/429/5xx, source-generated JSON
  serialisation for AOT/trim safety.
- `OrbitReceivePipeline.ReceiveAsync(projectId, versionId)`:
  resolve version → BFS the object tree (cache-first, max 8 parallel
  HTTP fetches, content-hash de-dup), pre-resolve every `@blob:HASH`
  texture in parallel, then convert.
- `ContentAddressedCache`: SHA256-keyed, two-char shard layout under
  `%LOCALAPPDATA%\PRISM.Visualiser\cache\{objects,blobs}\`. Atomic
  writes via temp file + rename.
- `BlobDownloader`: cache-first, parallel-on-miss with bounded
  concurrency, server-content integrity check on every fetch.

### glTF staging (BUILD.md §2)

- `MeshConverter`: Speckle face encoding `[n,v0..vn-1, n,v0...]` →
  fan-triangulated glTF index buffer; positions / normals / vertex
  colours / texcoords all round-trip.
- `MaterialConverter`: `RenderMaterial` → glTF PBR metallic-roughness
  with diffuse / base-colour / emissive / normal channels. The
  `@blob:HASH` placeholder scheme resolves through the run's blob map.
- `DataObjectConverter`: generic Speckle objects whose
  `displayValue` is a list of meshes (Brep / Curve fallbacks) flatten
  to a `StagedCollection` of `StagedMesh`es.
- `FallbackConverter`: anything not claimed by the above logs to
  `unknown_objects.jsonl` next to the staged glTF and surfaces as a
  `StagedUnknown`. Phase J (MVR / lighting) supersedes the fallback
  for the corresponding types.
- `CoordinateTransform`: ORBIT (Z-up RH, m) → UE (Z-up LH, cm).
  Mirror Y, scale by 100. Applied per-vertex during glTF write,
  never as a per-node transform.
- `SceneFlattener` + `GltfWriter` (SharpGLTF): one big glTF per
  import + a `scene_manifest.json` sidecar mapping each Speckle
  source object → glTF node index → layer path. Buffers + textures
  are written as external files; the writer round-trips the result
  through `ModelRoot.Load` with strict validation.

### CLI

- `stream` (without `--dry-run`) emits a new
  `prism-visualiser/staged/v1` JSON line on stdout the moment the
  glTF + manifest hit disk. Phase D/E will keep this event for the
  agent's progress UI.
- `stream --dry-run` is unchanged (still emits the original
  `prism-visualiser/ready/v1` line and exits 0).
- `--server prod | dev` now resolves to real URLs:
  `https://orbit.rebus.industries` / `https://orbit-dev.rebus.industries`.

### Tests

15 new xUnit tests (cache, mesh triangulation, coordinate transform,
material blob resolution, fallback sidecar, deep-tree round-trip,
hit/miss flow). The existing 4 ready-handshake tests continue to pass.

## v0.1.0 — Phase B scaffold

- System.CommandLine CLI with `stream` + `cache` subcommands
- `--dry-run` emits a syntactically valid `prism-visualiser/ready/v1`
  JSON event on stdout
- Job Object self-assignment with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
  for child-process supervision (Cirrus / UE land in Phase E/F)
- Structured logging via Serilog: console sink to stderr at Information,
  rolling file sink at Verbose under
  `%LOCALAPPDATA%\PRISM.Visualiser\runs\<runId>\logs\orchestrator.log`
- Content-addressed cache directory resolution under
  `%LOCALAPPDATA%\PRISM.Visualiser\cache\{objects,blobs,stage}`
  (no actual fetch / eviction yet)
- xUnit test project with `ReadyHandshakeTests` locking the on-wire
  JSON shape down field-by-field
