# PRISM Visualiser

Windows-native orchestrator that turns an ORBIT model version into a live
Unreal-Engine + Pixel-Streaming session. Designed to be spawned by the
PRISM Agent on a workstation that has UE installed; reads ORBIT objects +
blobs from the configured ORBIT server, imports them into a UE editor
session, brings up Cirrus (the signalling server), and hands the agent a
`playerUrl` + `signallingUrl` over stdout so the agent can publish the
ready state back to the PRISM server.

## Status — Phase C: ORBIT receive pipeline + glTF staging

The Phase B scaffold is now backed by a real receive pipeline:
authentication, content-addressed caching, parallel object + blob
fetches, Speckle-to-glTF conversion, and a manifest sidecar.

The `stream` subcommand without `--dry-run` runs end-to-end up to "glTF
on disk" — emits a `prism-visualiser/staged/v1` JSON line on stdout
and exits with code `9` (NotImplemented) until Phase D/E land the UE +
Cirrus bring-up. `--dry-run` is unchanged from Phase B.

Phases D – F will spawn UE + Cirrus on top of the staged glTF and emit
the final `prism-visualiser/ready/v1` line.

## Layout

```
visualiser/
├── PRISM.Visualiser.sln
├── Directory.Build.props                  ← shared Version, Nullable, LangVersion, etc.
├── CHANGELOG.md
├── src/PRISM.Visualiser.Orchestrator/
│   ├── PRISM.Visualiser.Orchestrator.csproj
│   ├── Program.cs                         ← System.CommandLine wiring
│   ├── Auth/                              ← Phase C
│   │   ├── IOrbitTokenSource.cs           ← env -> file -> fail chain
│   │   ├── EnvOrbitTokenSource.cs
│   │   ├── FileOrbitTokenSource.cs
│   │   └── CompositeOrbitTokenSource.cs
│   ├── OrbitApi/                          ← Phase C
│   │   ├── IOrbitApi.cs
│   │   ├── HttpOrbitApi.cs                ← bearer + Polly retry
│   │   ├── ContentAddressedCache.cs       ← SHA256, atomic writes
│   │   └── BlobDownloader.cs              ← parallel fetch w/ backpressure
│   ├── Pipeline/OrbitReceivePipeline.cs   ← BUILD.md §1
│   ├── Converters/FromOrbit/              ← BUILD.md §2 — Speckle → glTF
│   │   ├── IFromOrbitConverter.cs
│   │   ├── ConversionContext.cs
│   │   ├── UnknownObjectSink.cs
│   │   ├── MeshConverter.cs
│   │   ├── DataObjectConverter.cs
│   │   ├── MaterialConverter.cs
│   │   └── FallbackConverter.cs
│   ├── Staging/                           ← BUILD.md §2
│   │   ├── CoordinateTransform.cs         ← ORBIT (Z-up, m, RH) → UE (Z-up, cm, LH)
│   │   ├── SceneFlattener.cs
│   │   └── GltfWriter.cs                  ← SharpGLTF + scene_manifest.json
│   ├── Models/
│   │   ├── RunManifest.cs                 ← per-run immutable state
│   │   ├── ServerConfig.cs                ← prod / dev URLs (Phase C)
│   │   ├── ReadyEvent.cs                  ← "prism-visualiser/ready/v1"
│   │   ├── StagedEvent.cs                 ← "prism-visualiser/staged/v1" (Phase C)
│   │   ├── VersionDescriptor.cs           ← Phase C
│   │   ├── OrbitObject.cs                 ← Phase C — loose Speckle base
│   │   ├── StagedScene.cs                 ← Phase C
│   │   └── StagedNode.cs                  ← Phase C — sealed-record union
│   ├── Ipc/ReadyHandshake.cs              ← writes the JSON line to stdout
│   ├── Process/
│   │   ├── JobObject.cs                   ← Win32 KILL_ON_JOB_CLOSE
│   │   └── ProcessSupervisor.cs           ← log capture skeleton
│   ├── Logging/StructuredLog.cs           ← Serilog (stderr + file)
│   └── Cache/CacheRoot.cs                 ← %LOCALAPPDATA%\PRISM.Visualiser\cache
└── tests/PRISM.Visualiser.Orchestrator.Tests/
    ├── ReadyHandshakeTests.cs             ← Phase B
    ├── ContentAddressedCacheTests.cs      ← Phase C
    ├── MeshConverterTests.cs              ← Phase C — Smoke Test 3
    ├── CoordinateTransformTests.cs        ← Phase C — Smoke Test 4
    ├── ReceivePipelineTests.cs            ← Phase C — Smoke Tests 1 & 2
    ├── MaterialBlobResolutionTests.cs     ← Phase C — Smoke Test 5
    ├── FallbackConverterTests.cs          ← Phase C — Smoke Test 6
    └── TestHelpers/
        ├── FakeOrbitApi.cs                ← hand-rolled IOrbitApi mock
        └── TestEnv.cs                     ← per-test temp cache + helpers
```

## Build

From the [`REBUS-ORBIT/prism`](https://github.com/REBUS-ORBIT/prism) repo
root:

```powershell
dotnet build visualiser/PRISM.Visualiser.sln -c Release
```

Or just the orchestrator:

```powershell
dotnet build visualiser/src/PRISM.Visualiser.Orchestrator -c Release
```

## Test

```powershell
dotnet test visualiser/tests/PRISM.Visualiser.Orchestrator.Tests
```

## Run

### Phase C real receive (`stream` without `--dry-run`)

```powershell
$runId = [guid]::NewGuid().ToString()
$env:ORBIT_PAT_PROD = "<your prod PAT>"   # or sign in via the agent so the file store is populated
dotnet run --project visualiser/src/PRISM.Visualiser.Orchestrator -- `
  stream `
    --server prod `
    --project <projectId> `
    --model <modelId> `
    --version <versionId> `
    --run-id $runId `
    --signalling-port-hint 8888 `
    --json
```

Emits a `prism-visualiser/staged/v1` JSON line on stdout when the
glTF + manifest hit disk under
`%LOCALAPPDATA%\PRISM.Visualiser\cache\stage\<runId>\`, then exits with
code `9` (NotImplemented) until Phase D/E.

### Phase B dry-run (still supported)

```powershell
$runId = [guid]::NewGuid().ToString()
dotnet run --project visualiser/src/PRISM.Visualiser.Orchestrator -- `
  stream `
    --server prod `
    --project demo `
    --model demo `
    --version demo `
    --run-id $runId `
    --signalling-port-hint 8888 `
    --json `
    --dry-run
```

Sample output (single line on stdout, `\n` terminated):

```json
{"schema":"prism-visualiser/ready/v1","status":"ready","runId":"…","projectId":"demo","modelId":"demo","versionId":"demo","playerUrl":"http://127.0.0.1:0/","signallingUrl":"ws://127.0.0.1:0/","streamerId":"orbit_…","ueProcessId":0,"signallingProcessId":0,"logsDir":"C:\\Users\\…\\AppData\\Local\\PRISM.Visualiser\\runs\\…\\logs"}
```

Serilog goes to **stderr** (Information+) and to a rolling file under
`%LOCALAPPDATA%\PRISM.Visualiser\runs\<runId>\logs\orchestrator.log`
(Verbose+).

## CLI surface

```
prism-visualiser stream
  --server <prod|dev>            ORBIT environment selector
  --project <id>                 ORBIT project id
  --model <id>                   ORBIT model id
  --version <id>                 ORBIT version id
  --run-id <uuid>                caller-supplied run UUID
  --signalling-port-hint <port>  preferred Cirrus port
  --json                         required; ready event is JSON on stdout
  [--dry-run]                    skip real work; emit fake ready event

prism-visualiser cache prune
  --older-than <duration>        e.g. 14d, 12h, 30m (stub in Phase B)
```

## Job Object semantics

`Program.Main` creates a Win32 Job Object with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and assigns the current process to
it before parsing any arguments. `JobObject.AddProcess(pid)` is the
entry point Phase E/F will call for the Cirrus + UE child processes the
real `stream` path spawns. If the orchestrator dies for any reason — a
caller kills its parent agent shell, the OS reaps it, an unhandled
exception escapes Main — the children die with it. No orphan UE / Cirrus
processes ever.

## Versioning

The orchestrator versions independently of the PRISM Agent. Single
source of truth lives in
[`Directory.Build.props`](./Directory.Build.props):

```xml
<VisualiserVersion>0.1.0</VisualiserVersion>
```

CI publishes a release on a tag matching `visualiser-v<VisualiserVersion>`
to
[`REBUS-ORBIT/prism-visualiser`](https://github.com/REBUS-ORBIT/prism-visualiser).

## Links

- Plan: `.cursor/plans/prism_visualiser_role_d36fa628.plan.md`
- Companion: `BUILD.md` §10 ("Server → agent WS")
- Changelog: [./CHANGELOG.md](./CHANGELOG.md)
