# PRISM Visualiser changelog

The orchestrator versions independently of the PRISM Agent. The bump is
`Directory.Build.props::VisualiserVersion`; the CI tag convention is
`visualiser-v<VisualiserVersion>`.

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
