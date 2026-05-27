# PRISM Visualiser changelog

The orchestrator versions independently of the PRISM Agent. The bump is
`Directory.Build.props::VisualiserVersion`; the CI tag convention is
`visualiser-v<VisualiserVersion>`.

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
