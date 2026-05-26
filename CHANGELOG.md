# Changelog

All notable changes to **PRISM** (server + agent + connector submodule) live
here. Versions tagged on this repo are agent versions; server image tags follow
the same numbering when bumped, otherwise server ships as rolling deploys off
`main` via the `server-image` workflow.

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## v0.1.31 — 2026-05-26

Web UI hardening: fixes the Save → 500 ACL crash, makes LAN access work
out of the box, restyles the page to match the PRISM admin UI, and adds
proper Start Menu shortcuts.

### Fixed

- **agent-config save 500 (`Access to the path '...agent-config.json' is
  denied`)**: the scheduled task runs as the interactive workstation
  user, which on most Rhino boxes is *not* a local administrator and
  therefore cannot write to `C:\Program Files\PRISM.Agent\`. The
  config now lives at `C:\ProgramData\PRISM.Agent\agent-config.json`
  (user-writable). `Load()` checks ProgramData first and falls back to
  the legacy Program Files path; `Save()` always targets ProgramData
  and best-effort deletes a stale legacy file. A
  `UnauthorizedAccessException` on save falls through to
  `%LOCALAPPDATA%\PRISM.Agent\agent-config.json` so the agent never
  silently drops an operator's edit on locked-down boxes.

- **install.ps1**: writes the initial `agent-config.json` to ProgramData
  instead of Program Files; auto-migrates an existing legacy config on
  upgrade; runs `icacls` to ensure Authenticated Users have Modify on
  `C:\ProgramData\PRISM.Agent\`.

### Changed

- **LAN access default** (`webUiBindAll: true`): the agent now binds the
  web UI to `http://+:7421/` out of the box so operators can configure
  any workstation from a browser tab on a different machine.
  `install.ps1` pre-registers a URL ACL
  (`netsh http add urlacl url=http://+:7421/ user="NT AUTHORITY\Authenticated Users"`)
  and a `New-NetFirewallRule` so the (non-elevated) agent can bind and
  the LAN can reach it. Pass `-WebUiLocalhostOnly` to skip both for
  hardened deploys; `uninstall.ps1` reverses both.

- **web UI styling**: rebuilt the embedded HTML/CSS to mirror
  `web/src/shared/designSystem.css` exactly -- ORBIT primary `#e06238`
  on neutral foundation greys, light + dark themes via
  `[data-theme="dark"]` on `<html>`, theme choice persisted under the
  same `prism.theme` localStorage key the SPA uses, header logo +
  status pill, card layout, monospaced format chips. The page now
  visually belongs in the same family as the admin pages at
  `prism.rebus.industries`.

- **Inno installer (`PRISM.Agent.iss`)**: adds proper Start Menu
  shortcuts (`PRISM Agent` → launches the tray app, `Web UI` → opens
  the browser page, `Uninstall PRISM Agent`) and an optional Desktop
  shortcut behind a `[Tasks]` checkbox (default off). `AllowNoIcons=no`
  guarantees the Start Menu group is created.

### Added

- **install.ps1**: `-WebUiPort` and `-WebUiLocalhostOnly` parameters so
  unattended deploys can pick the bind port and disable LAN access in
  one shot.

---

## v0.1.30 — 2026-05-26

Adds a real Windows wizard installer (`PRISM.Agent-Setup-vX.Y.Z.exe`,
built with Inno Setup) so workstation install no longer requires
"download zip → unblock → expand → invoke install.ps1" plumbing.

### Added

- **wizard installer** (`PRISM/agent/install/PRISM.Agent.iss`): Inno
  Setup script that wraps the multi-file publish payload + `install.ps1`
  + `uninstall.ps1` into a single signed .exe. Wizard pages: install
  dir picker, **PRISM connection settings** (server URL / node name /
  slots, with sensible defaults and validation), and a finish page with
  optional "open web UI" / "launch agent" checkboxes. Upgrades preserve
  the existing `agent-config.json`. AppId is fixed
  (`{8F3D9A12-7E5C-4B11-A0F2-9D1E3C7B5142}`) so reinstalls and version
  bumps perform in-place upgrades. The wizard runs
  `install.ps1 -LaunchNow` under the hood, so post-install state matches
  the legacy zip flow exactly (same scheduled task, same config file
  shape, same `webUiPort`/`webUiBindAll` defaults).

### Changed

- **CI** (`.github/workflows/agent.yml`): `windows-latest` runner now
  also compiles the .iss script (Inno Setup is pre-installed there;
  Chocolatey fallback in case it isn't) and attaches the resulting
  `PRISM.Agent-Setup-v*.exe` to the GH release alongside the zip. The
  zip is uploaded **first** so it lands as `assets[0]` for older agents
  whose Updater blindly grabs `assets[0]`. New `Sign installer` step
  signs the wrapper .exe when `CODE_SIGN_CERT` / `CODE_SIGN_PASSWORD`
  secrets are configured.

- **install.ps1**: now idempotent for the in-place case (skips the
  payload copy when invoked from inside the install dir, which is what
  the Inno wizard does). On upgrade, preserves `agent-config.json`
  unless `-ForceConfig` is passed. Default config now writes the new
  `webUiPort` / `webUiBindAll` keys and the full `roles` array.

- **uninstall.ps1**: new `-NoFileCleanup` switch lets the host
  uninstaller (Inno's `[UninstallDelete]`) own the on-disk wipe so the
  script cannot self-delete its own parent directory mid-run.

### Fixed

- **Updater**: `CheckForUpdateAsync` now picks the `.zip` asset by
  filename instead of blindly grabbing `assets[0]`, so a release that
  carries both `PRISM.Agent-v*.zip` and `PRISM.Agent-Setup-v*.exe` no
  longer breaks in-app self-update if the upload order changes.

---

## v0.1.29 — 2026-05-26

Gives the agent a local **web UI** so operators can configure all
settings and pause/resume the watcher from a browser instead of
RDP-ing into each workstation to use the WinForms tray. Also fixes
the heartbeat that has been reporting `slotsBusy=0` since phase 3.

### Added

- **agent web UI** (`PRISM/agent/.../WebUi/`): the agent now serves a
  single-page configuration site on `http://localhost:7421/` (right-click
  the tray icon → **🌐 Open Web UI**). Backed by a tiny `HttpListener`
  hosted service plus an `AgentControlPlane` singleton that the tray + the
  web UI both mutate, so `nodeName`, `slots`, `roles`, watcher pause/resume,
  Rhino version, log dir, and the web UI's own `webUiPort` /
  `webUiBindAll` flags can all be edited live. Routes:
  `GET /api/state`, `POST /api/config`, `POST /api/watcher/pause|resume`,
  `GET /api/logs?n=N`. Live-applied (no restart): `nodeName`, `slots`,
  `roles`, `logDir`. Restart-required: `prismUrl`, `rhinoVersion`,
  `webUiPort`, `webUiBindAll`. Defaults to `localhost`-only binding;
  flip `webUiBindAll: true` to expose on the LAN (no auth).

### Fixed

- **agent heartbeat**: `HeartbeatData.slotsBusy` now reports the real
  `WorkerSlotPool.BusyCount` instead of the hard-coded `0` placeholder
  left in since the phase 3 scaffold, so the admin dashboard's
  concurrency stat finally matches reality.

- **install docs**: `AGENT_INSTALL.md` download link now points at
  `REBUS-ORBIT/prism-agent/releases/latest` (matches the in-app
  Updater's poll URL).

---

## v0.1.28 — 2026-05-26

Adds the `prism-assimp` pre-conversion sidecar so uploads in the
glTF/Collada/Blender/USDZ/DirectX/PLY/STL family no longer get rejected
at the validation gate. The sidecar lives next to `prism-server` in the
compose stack, accepts the source file over HTTP, and returns an
OBJ+MTL+textures zip that the existing Rhino agent path already knows
how to ingest.

### Added

- **assimp service** (`PRISM/assimp/`): new FastAPI app shipped as
  `ghcr.io/rebus-orbit/prism-assimp:latest`, multi-stage Docker image
  built on Debian bookworm + libassimp 5.4.3 from source +
  Python 3.11 + pyassimp 4.1.4. Exposes `GET /health`,
  `GET /v1/formats`, and `POST /v1/preconvert` (multipart with
  `file`, `target_unit`, optional `bundle.zip`, `return_mode=stream|json`).
- **server (api / conversion)**: `PRISM/server/src/conversion/preconvert.ts`
  is a small dispatcher that recognises Assimp-eligible extensions
  (`.gltf .glb .dae .blend .x .usdz .ply .stl`), POSTs the upload to
  `${ASSIMP_SERVICE_URL}/v1/preconvert` (with `return_mode=stream`),
  saves the resulting zip into `UPLOAD_DIR`, unlinks the original, and
  returns a `preconvertMeta` summary that gets stored under
  `jobs.options.preconvert` so the admin UI can still show "you uploaded
  a .glb" while the agent works against the .zip on the job row.
  `PRISM/server/src/api/convert.ts` extends `SUPPORTED_EXTS` and runs
  this between the upload and the BullMQ enqueue. The `preconvert`
  stage also lights up in the static pipeline topology so the admin
  Pipeline view renders it.
- **infra**: `infra/docker-compose.yml` adds a `prism-assimp` service
  (image `ghcr.io/rebus-orbit/prism-assimp:${PRISM_ASSIMP_TAG:-latest}`,
  curl-based healthcheck on `:8088/health`, `prism-assimp-work` named
  volume on `/work`). `prism-server` now defaults `ASSIMP_SERVICE_URL`
  to the in-network compose URL (`http://prism-assimp:8088`); leaving
  the env var explicitly empty disables the pre-conversion path
  entirely and falls back to the previous "validation rejects this
  extension" behaviour.
- **CI**: `.github/workflows/assimp.yml` builds + pushes the sidecar
  image to GHCR on changes under `assimp/**`, then deploys via the
  LAN-local self-hosted runner (`[self-hosted, prism-deploy]`) using
  the same `ssh prism-prod` alias as `server.yml`. Both `server.yml`
  and `assimp.yml` deploy steps now also `scp infra/docker-compose.yml`
  to `/opt/prism/` (which is not a git checkout on the VM) so future
  service-list changes actually apply.

### Fixed

- **assimp service**: cold-start crashed five different ways before
  reaching steady state -- captured here so the next
  Python-FastAPI-pyassimp service can skip ahead:
  `LOG_LEVEL=info` (lowercase, from the orchestrator default) crashes
  `logging.basicConfig` (case-sensitive), so it's `.upper()`-d on
  startup; FastAPI rejects the `JSONResponse | FileResponse` union
  return on `/v1/preconvert` so the route declares
  `response_model=None`; pyassimp 4.1.4 imports stdlib `distutils`
  which Python 3.12 removed (PEP 632), so the runtime stage pins
  `python:3.11-slim-bookworm`; pyassimp's `Scene` is not a context
  manager, so the converter uses the documented `try/finally +
  pyassimp.release(scene)` pattern; pyassimp 4.1.4's
  `node.transformation` numpy view of `aiMatrix4x4` is shifted in
  memory and even identity matrices come back corrupt, so the
  converter asks Assimp to bake every node's world transform into
  the mesh vertices themselves
  (`aiProcess_PreTransformVertices`) and uses identity for all
  per-leaf transforms. Trade-off: layer hierarchy degrades to "one
  OBJ group per scene mesh" until pyassimp's matrix decode is fixed.
- **assimp service — Collada validator + error reporting**:
  `aiProcess_ValidateDataStructure` was rejecting valid
  Rhino-exported Collada files (e.g. duplicate camera names from
  `View-Front`/`View-Top`/`View-Right`/`View-Front` duplicates),
  surfacing as a generic `pyassimp.AssimpError("Could not import
  file!")`. Dropped the validator from the default flag set and
  added an `_assimp_last_error()` helper that calls
  `aiGetErrorString` over ctypes so the orchestrator now sees the
  real `libassimp` reason in the exception message.
- **assimp service — Collada layer names**: Rhino exports its
  layer names on `<node name="Brep">`-style attributes, but
  Assimp's Collada loader puts the synthetic `<node id="...">`
  GUID in `aiNode.mName` and pyassimp 4.1.4 then truncates that
  by 4-8 characters on top, so the layer picker rendered six
  UUID-looking strings per upload. Two-part fix: `layers.py` now
  parses the Collada XML directly with stdlib
  `xml.etree.ElementTree`, walking *every* `<node>` (including
  those parked under `<library_nodes>` for block / instance
  definitions) to build a `geometry-id -> human node-name` map,
  and `walk_leaves` does suffix-match lookup against that map to
  paper over pyassimp's truncation bug. Result for the standard
  Rhino test export: layer picker shows `Default / Brep /
  Extrusion / Brep_1 / Extrusion_1 / Brep_2` instead of the
  previous UUID soup. Other formats fall back to the corrected
  `decode_aistring()` helper that reads the C `aiString` layout
  properly via raw `ctypes.addressof`.

---

## v0.1.27 — 2026-05-26

Consolidation release. Bakes in every hotpatch carried by VM 211 and PC02
since v0.1.26 so we have a clean rollback target.

### Fixed

- **server (ws)**: `progress` handler no longer downgrades a job that has
  already reached a terminal state. Previously a fire-and-forget
  `progress?.Report(("Done", 100))` from the Rhino connector could land
  *after* the `Complete` message and clobber `jobs.status` from `complete`
  back to `processing`, leaving the UI spinning forever on jobs that
  actually succeeded. `PRISM/server/src/ws/agentProtocol.ts` now adds
  `notInArray(jobs.status, ['complete','failed','cancelled'])` to the
  progress UPDATE and bails out (with a debug log) when 0 rows match.
- **server (api)**: `swapYZ`, `selectLayers`, and `includeLayerDescendants`
  now parse correctly. `z.coerce.boolean()` was treating the string
  `"false"` posted by the form as truthy (any non-empty string coerces to
  `true`), so unchecking the Y/Z swap checkbox in the convert UI silently
  re-enabled the rotation. Replaced with a `formBool()` preprocess that
  string-compares case-insensitively to `"true"` before coercion in both
  `PRISM/server/src/api/convert.ts` and `PRISM/server/src/v1/routes.ts`.
- **agent (connector / vendor SDK)**: zero-valued numeric properties on
  geometry DTOs (e.g. `Point.Y = 0.0`) were being dropped on the wire by
  `DefaultValueHandling.Ignore`. For DWG geometry on the XZ-plane after
  the Y/Z swap that meant every `Line.start/.end` arrived without a `y`
  key, which the viewer rendered as `NaN` (i.e. invisible). Flipped
  `OrbitJsonSettings.Default` to `DefaultValueHandling.Include`.
- **agent (connector / vendor SDK)**: 16 geometry / primitive / proxy
  DTOs were inheriting the generic `OrbitBase.OrbitType`, so they
  serialised with `speckle_type = "Objects.Base"` and the viewer fell
  back to its dumb-renderer path. Added explicit `OrbitType` overrides
  on `Line`, `Polyline`, `Arc`, `Circle`, `NurbsCurve`, `PolyCurve`,
  `Plane`, `Surface`, `PointCloud`, `Interval`, `Vector3d`, `Transform`,
  `DefinitionProxy`, `RenderMaterialProxy`, `GroupProxy`, `ColorProxy`.

### Changed

- **agent (connector)**: deduplication phase rewritten to run the HEAD
  probes in parallel chunks of 16 with continuous progress reporting.
  A representative DWG with 6 137 objects went from ~8 minutes of silent
  "checking server…" (sequential HEAD requests over ~80 ms RTT) down to
  ~30 seconds with a visible `Checking server… N/M (K new)` ticker.
  Also dropped the redundant `progress?.Report(("Done", 100))` at the
  end of `SendAsync` that was racing the `Complete` message.

### Added

- **agent (Rhino)**: `SiblingTextureHydrator`. When `FileFbx.Read` returns
  a doc with `Materials.Count == 0` but textures are sitting next to the
  FBX inside the extracted .zip bundle (common pattern for "FBX + textures
  in a folder" exports out of DCC tools), the agent now synthesises a PBR
  material from sibling files matching `*baseColor*`, `*albedo*`,
  `*diffuse*`, `*normal*`, `*roughness*`, `*metallic*` etc. and assigns
  it to every imported object. Wired in from `RhinoFileOpener` after a
  successful read.
- **web (admin)**: dedicated `/admin/logs` page with API-call log
  streaming. Replaces the earlier floating-panel prototype. Admin-only,
  shows last N requests with status, duration, and principal.

### Submodule

- `vendor/orbit-monorepo` → `e942678` on `prism-connector-fixes`.
  Carries the three connector / SDK fixes above (parallel dedup +
  late-progress drop + `DefaultValueHandling.Include` + `OrbitType`
  overrides).

### Notes

This release replaces the hotpatched DLLs on PC02 and the manually-built
`prism-server:local` image on VM 211. After deploying `v0.1.27`:

- `ghcr.io/rebus-orbit/prism-server:v0.1.27` is the canonical server image.
- `PRISM.Agent-v0.1.27.zip` (from the `REBUS-ORBIT/prism-agent` release)
  is the canonical agent installer; re-run `install.ps1` on PC02.

---

## v0.1.26 — 2026-05-25

### Fixed

- **agent**: `swap_yz` matrix flipped from `Rx(-90°)` to `Rx(+90°)`
  (`PRISM/agent/src/PRISM.Agent/Pipeline/RhinoAxisSwap.cs`). With our standard
  Y-up OBJ test bundle, `Rx(-90°)` from v0.1.25 landed the model upside-down.
  `Rx(+90°)` produces `(x, y, z) → (x, -z, y)`; determinant is still `+1`, so
  triangle winding, normals, and UVs stay consistent.

### Matrix history (for the curious)

| Version  | Matrix                            | Det | Result on Y-up OBJ test bundle      |
|----------|-----------------------------------|-----|-------------------------------------|
| v0.1.24  | reflection `(x,y,z) → (x,z,y)`   | -1  | mirrored — front faced *away*       |
| v0.1.25  | `Rx(-90°)`  `(x,y,z) → (x,z,-y)` | +1  | rotated, but upside-down            |
| v0.1.26  | `Rx(+90°)`  `(x,y,z) → (x,-z,y)` | +1  | **right-side-up, front-facing**     |

**Note**: tag `v0.1.25` and `v0.1.26` both point at commit `2355ddb`. The
commit subject line was written before the `Rx(-90°) → Rx(+90°)` flip and
still reads "now applies -90 degree X rotation". The code in that SHA is
v0.1.26 (`Rx(+90°)`). Verified visually against the test bundle at
`https://orbit.rebus.industries/projects/932088aa79/models/683af13566`
(screenshots in [`docs/swap-yz-v0.1.26/`](docs/swap-yz-v0.1.26/)).

---

## v0.1.25 — 2026-05-25

### Changed

- **agent**: `swap_yz` matrix replaced reflection with `Rx(-90°)` rotation
  to preserve handedness. Superseded by v0.1.26 — see matrix history above.

---

## v0.1.24 — 2026-05-25

### Fixed

- **agent**: `swap_yz` is now actually applied. The flag was UI-wired and
  threaded through the server/agent contract, but the agent dropped it on the
  floor. New `RhinoAxisSwap.ApplyYZSwap(RhinoDoc)` runs between
  `RhinoFileOpener.OpenInto` and `RhinoSendPipeline.SendAsync`, gated on
  `AssignData.Options?.SwapYZ == true`. Single doc-table transform
  (`doc.Objects.Transform(id, swap, deleteOriginal: true)`) so block instance
  placements ride along. The shipped matrix in this version was a reflection
  (det = -1); the resulting handedness flip caused front-facing geometry to
  render as if seen from behind. Replaced in v0.1.25.

---

## v0.1.23 — 2026-05-24

### Added

- **agent**: `.zip` bundle uploads for OBJ + MTL + textures (and any sidecar
  bundle).  New `PRISM.Agent.Rhino.ZipBundleExtractor` expands archives next
  to the downloaded source, picks the primary geometry file by extension
  priority, and feeds Rhino's importer with the on-disk siblings intact so
  `map_Kd` paths in `.mtl` resolve. Safety caps: **2 GiB cumulative**,
  **1 GiB per entry**, zip-slip protection on every extracted path.
- **server**: `SUPPORTED_EXTS` accepts `.zip`.
- **web**: file picker `accept=` includes `.zip`.
- **contract**: `Hello.SupportedFormats` advertises `.zip` to dispatchers.

### Known limitations

- MTL and `map_Kd` paths inside the archive **must** be relative to the OBJ's
  directory inside the zip. The agent does not rewrite paths.

### Submodule

- `vendor/orbit-monorepo` → `0e76a3b` on `prism-connector-fixes`. Brings in
  the connector texture / UV fixes (basecolor classifier, opaque-white
  diffuse promotion, missing-texture warnings, render-mesh merging across
  the OBJ+MTL+texture path).

---

## server hotpatch `92d1c8c` — 2026-05-24

### Fixed

- **server (ws)**: `pollLayers` no longer gets stuck on `"walking layer
  table"`. `PRISM/server/src/ws/agentProtocol.ts` had a fire-and-forget
  `socket.on('message', …)` that allowed Progress and Layers DB writes to
  race; whichever landed second won, often clobbering the Layers result back
  to `extracting-layers` / `"walking layer table"`. Fix serialises
  per-connection handlers via a `pendingHandler` promise chain so
  WS-receive order equals DB-write order.

Hotpatched onto VM 211 immediately, then deployed via the `server-image`
workflow.

---

## Older

See `git log` / `git tag -n` for v0.1.22 and earlier (multi-file publish for
hotpatch, `.3dm` open-typed API, headless RhinoCore template, etc.).
