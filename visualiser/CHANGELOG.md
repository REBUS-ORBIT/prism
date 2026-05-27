# PRISM Visualiser changelog

The orchestrator versions independently of the PRISM Agent. The bump is
`Directory.Build.props::VisualiserVersion`; the CI tag convention is
`visualiser-v<VisualiserVersion>`.

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
