# PRISM Visualiser Orchestrator

`prism-visualiser.exe` — a Windows-only .NET 8 console app that the PRISM
agent spawns once per visualiser run. It downloads an ORBIT version,
stages it to glTF, scaffolds a fresh Unreal Engine 5.7 project from a
cached template, headlessly imports the geometry via the UE Python API,
launches the editor in `-game` mode under Pixel Streaming 2, and emits a
ready JSON to stdout for the agent to forward.

**Status:** production-ready as of `v0.2.0` (orchestrator
`visualiser-v0.5.0` / milestone alias `visualiser-v0.2.0`).

---

## Where it fits

```
PRISM server (VM 211)
   │  startVisualisation envelope (WSS)
   ▼
PRISM.Agent.exe  (on a workstation with `can_visualise = true`)
   │  spawn child process
   ▼
prism-visualiser.exe   <-- this project
   │
   ├─► ORBIT API     (HTTPS — version + blobs)
   ├─► glTF staging  (SharpGLTF)
   ├─► UE template   (cached zip from REBUS-ORBIT/orbit-ue-template)
   ├─► UnrealEditor-Cmd.exe -run=PythonScript import_orbit.py
   ├─► (optional) import_mvr.py — Phase J DMX/MVR branch
   ├─► UnrealEditor.exe -game -PixelStreaming2URL=ws://127.0.0.1:<port>
   ├─► node.exe Cirrus signalling
   └─► stdout: prism-visualiser/ready/v1 envelope
```

Project layout per BUILD.md §"Project layout":

```
visualiser/
├── PRISM.Visualiser.sln
├── Directory.Build.props                (VisualiserVersion source-of-truth)
├── src/PRISM.Visualiser.Orchestrator/
│   ├── Program.cs                       (System.CommandLine — `stream`, `--dry-run`)
│   ├── Auth/                            (ORBIT bearer token sources)
│   ├── OrbitApi/                        (HTTP client + content-addressed cache)
│   ├── Pipeline/{OrbitReceive,Visualiser}Pipeline.cs
│   ├── Converters/FromOrbit/            (Mesh, DataObject, Material, Fallback)
│   ├── Staging/{Gltf,Coordinate,SceneFlattener}.cs
│   ├── Unreal/
│   │   ├── UnrealEnvironment.cs         (UE root discovery)
│   │   ├── TemplateFetcher.cs           (orbit-ue-template release zip cache)
│   │   ├── ProjectScaffolder.cs         (per-run .uproject clone)
│   │   ├── UnrealLauncher.cs            (UnrealEditor-Cmd.exe + -game spawn)
│   │   ├── GpuPreflight.cs              ★ Phase K hardening
│   │   ├── MvrGdtfDetector.cs           (Phase J)
│   │   └── PythonScripts/import_{orbit,mvr}.py(.in)
│   ├── PixelStreaming/                  (Cirrus supervisor + PortAllocator)
│   ├── Process/{JobObject,ProcessSupervisor}.cs
│   ├── Ipc/ReadyHandshake.cs
│   └── Logging/StructuredLog.cs
└── tests/PRISM.Visualiser.Orchestrator.Tests/
    └── ... (xunit; ~60 unit tests across all phases)
```

---

## Build

```powershell
# From the repo root:
dotnet build visualiser/PRISM.Visualiser.sln -c Release -warnaserror

# Tests:
dotnet test  visualiser/PRISM.Visualiser.sln -c Release --logger "console;verbosity=minimal"

# Publish a self-contained EXE (sidecar bundled into the agent installer):
dotnet publish visualiser/src/PRISM.Visualiser.Orchestrator/PRISM.Visualiser.Orchestrator.csproj `
    -c Release -r win-x64 -o publish/visualiser/
```

CI does the same thing in `.github/workflows/visualiser-msi.yml` on every
push to `visualiser-v*` tags and uploads `prism-visualiser-vX.Y.Z.exe` to
the matching GitHub release.

---

## CLI

```text
prism-visualiser.exe stream \
    --server prod \
    --project <project-id> \
    --model   <model-id> \
    --version <version-id> \
    --run-id  <agent-generated-uuid> \
    --signalling-port-hint <port> \
    [--template-tag v1.0.0-ue5.7] \
    [--strict-gpu] \
    [--json] \
    [--dry-run]
```

| Flag                       | What                                                                                                        |
| -------------------------- | ----------------------------------------------------------------------------------------------------------- |
| `--server prod\|dev`       | Which ORBIT cluster to fetch the version from.                                                              |
| `--project`/`--model`/`--version` | ORBIT identifiers. `--version` omitted = latest.                                                     |
| `--run-id`                 | Agent-generated UUID. Used as the directory name under `runs/`, the streamer id prefix, and the ready JSON. |
| `--signalling-port-hint`   | Preferred Cirrus port. Falls back to `PortAllocator` if taken.                                              |
| `--template-tag`           | Pin a specific UE template tag. Defaults to the agent-supplied default.                                     |
| `--strict-gpu`             | Phase K: hard-reject when `nvidia-smi` is not on PATH. Default off — non-NVIDIA boxes get a soft warning.   |
| `--json`                   | Emit machine-readable Phase events to stdout (`staged/v1`, `imported/v1`, `ready/v1`, `failed/v1`).         |
| `--dry-run`                | Phase B compatibility: emit a fake `ready/v1` without touching ORBIT, UE, or Cirrus. Used by the agent for self-test. |

---

## Exit codes

| Code | When                                                                                          |
| ---- | --------------------------------------------------------------------------------------------- |
|  0   | Success — `ready/v1` emitted.                                                                |
|  1   | Generic failure — see stderr.                                                                |
|  2   | Bad CLI arguments.                                                                            |
|  3   | ORBIT auth failure (no token, expired, invalid).                                              |
|  4   | ORBIT receive failure (version not found, blob download error after retries).                 |
|  5   | UE template fetch / extract failure.                                                          |
|  6   | UE editor import failure (`import_orbit.py` threw).                                           |
|  7   | UE editor launch failure (couldn't spawn UnrealEditor.exe).                                   |
|  8   | Cirrus signalling failure.                                                                    |
|  9   | (reserved)                                                                                    |
| **10** | **GPU pre-flight rejected the run.** See [`Unreal/GpuPreflight.cs`](src/PRISM.Visualiser.Orchestrator/Unreal/GpuPreflight.cs) and [`docs/ANTIVIRUS_EXCLUSIONS.md`](../docs/ANTIVIRUS_EXCLUSIONS.md). |

The agent maps exit code → `prism-visualiser/failed/v1` envelope with a
matching `code` field (`gpu_preflight_failed` for 10, `import_failed`
for 6, etc.); see `Models/FailedEvent.cs`.

---

## Hardening (Phase K)

-   **GPU pre-flight.** `GpuPreflight.cs` runs before `ImportAsync` and
    refuses to start if (a) the workstation has < 4 GB free VRAM or
    (b) a stale `UnrealEditor*.exe` is already running (would clash for
    the NVENC encoder). Soft-fails when `nvidia-smi` is missing unless
    `--strict-gpu` is set. Test coverage:
    [`tests/.../GpuPreflightTests.cs`](tests/PRISM.Visualiser.Orchestrator.Tests/GpuPreflightTests.cs).
-   **AV exclusions.** UE shader compile + per-run template extract are
    hot paths for Windows Defender. The agent ships a helper at
    `agent/install/Set-VisualiserAvExclusions.ps1` and the rationale
    lives in [`docs/ANTIVIRUS_EXCLUSIONS.md`](../docs/ANTIVIRUS_EXCLUSIONS.md).
-   **Scheduled-task resilience.** The agent's installer registers
    AtLogon + AtStartup triggers with `RestartCount=3 / RestartInterval=1m`,
    so a crashed orchestrator never leaves a workstation agentless for
    long. See [`docs/SCHEDULED_TASK_RESILIENCE.md`](../docs/SCHEDULED_TASK_RESILIENCE.md).

---

## Versioning

This subtree versions independently from the agent + server via the
`visualiser-vX.Y.Z` tag pattern. Source-of-truth is
`visualiser/Directory.Build.props` (`<VisualiserVersion>`). The
`.github/workflows/visualiser-msi.yml` workflow filters on
`visualiser-v*` so it doesn't cross-fire with `agent.yml`/`server.yml`.

The v0.2.0 milestone ships orchestrator `visualiser-v0.5.0` aliased as
`visualiser-v0.2.0` on the same commit. See
[`../docs/RELEASE_STRATEGY.md`](../docs/RELEASE_STRATEGY.md) for the
full strategy.

---

## See also

-   [`docs/PORTAL_INTEGRATION.md`](../docs/PORTAL_INTEGRATION.md) —
    third-party integrator guide (the customer-facing API contract).
-   [`docs/VISUALISER_MERGE_ORDER.md`](../docs/VISUALISER_MERGE_ORDER.md) —
    PR merge order for the v0.2.0 milestone.
-   [`docs/RELEASE_STRATEGY.md`](../docs/RELEASE_STRATEGY.md) — milestone
    + maintenance release strategy.
-   [`docs/ANTIVIRUS_EXCLUSIONS.md`](../docs/ANTIVIRUS_EXCLUSIONS.md) —
    workstation tuning.
-   [`docs/SCHEDULED_TASK_RESILIENCE.md`](../docs/SCHEDULED_TASK_RESILIENCE.md) —
    agent process supervision.
-   `https://prism.rebus.industries/docs` — Redoc-rendered OpenAPI 3.1.
