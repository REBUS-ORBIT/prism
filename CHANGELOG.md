# Changelog

All notable changes to **PRISM** (server + agent + connector submodule) live
here. Versions tagged on this repo are agent versions; server image tags follow
the same numbering when bumped, otherwise server ships as rolling deploys off
`main` via the `server-image` workflow.

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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
