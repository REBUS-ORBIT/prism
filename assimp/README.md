# prism-assimp

Server-side **pre-conversion sidecar** for PRISM. Takes any file that Assimp
can read and emits the format that the PRISM agent's Rhino pipeline already
knows how to convert (`OBJ + MTL + textures.zip`), so adding "support for a
new format" usually reduces to "Assimp can already read it".

## Why a sidecar

- PRISM server is TypeScript/Node, but the most ergonomic Assimp bindings live
  in C++ / Python / .NET. A small Python service keeps the server image lean
  and lets us pin Assimp + texture-handling deps independently.
- Crashes in Assimp (it has corner cases) are isolated from the orchestrator.
- Horizontal scale: spin up N replicas of `prism-assimp` for big batches.

## Pipeline placement

```
upload (HTTP, web UI)
  -> POST /v1/convert  (prism-server)
       |
       +-- ext is one of {3dm, dwg, dxf, obj/zip, fbx/zip}  ── existing path ──> agent (Rhino)
       |
       +-- ext is one of ASSIMP_PRECONVERT_EXTS              ── new path ──>
                |
                v
            prism-assimp:  POST /preconvert  (multipart)
                | <-- returns <job>.zip containing model.obj + model.mtl
                |     + textures/*.png + manifest.json
                v
            prism-server stores the zip as the canonical input for the job
                |
                v
            dispatch to agent (Rhino) using the existing OBJ+MTL+textures.zip
            pipeline.  Agent treats it identically to a user-uploaded zip.
```

## Layer preservation

Assimp parses every supported format into the same in-memory shape:

- `aiScene.mRootNode`: tree of `aiNode`s. Each `aiNode` has a name, a 4x4
  transform, and an array of mesh indices.
- `aiScene.mMeshes`: flat array of `aiMesh`. Each mesh references a material
  index.
- `aiScene.mMaterials`: flat array of `aiMaterial` with PBR + legacy
  properties (`AI_MATKEY_BASE_COLOR`, `AI_MATKEY_TEXTURE(DIFFUSE, 0)`, etc.).

We walk the node tree depth-first and emit one OBJ `g <slash/separated/path>`
per leaf node, applying the accumulated transform to the leaf's mesh
vertices.  The OBJ + `usemtl` pairing carries material assignment.  When the
agent imports this OBJ on PC02, Rhino interprets each `g` as a layer (or a
nested layer path if it sees `/` separators), preserving the source
hierarchy down to the leaf.

## Format coverage (Phase 1 wishlist)

| Group       | Extensions                              | Notes                                                              |
| ----------- | --------------------------------------- | ------------------------------------------------------------------ |
| GLTF / GLB  | `.gltf`, `.glb`                         | full PBR; textures inline (`.glb`) or external (`.gltf` + folder)  |
| STL         | `.stl`                                  | binary + ascii; mesh-only, no materials                            |
| PLY         | `.ply`                                  | binary + ascii; vertex colors → fake material                      |
| COLLADA     | `.dae`                                  | layer-heavy; large in the wild                                     |
| Blender     | `.blend`                                | best-effort; Blender's binary format is partially documented       |
| DirectX X   | `.x`                                    | legacy DirectX retained mode                                       |
| USDZ        | `.usdz`                                 | Assimp ≥ 5.3                                                       |
| Wavefront   | `.obj`                                  | only if we want server-side pre-conv to fix mtl/texture paths      |
| Autodesk FBX| `.fbx`                                  | only as a fallback path when Rhino's FileFbx loses materials       |

`SUPPORTED_EXTENSIONS` matrix updates land in `PRISM/shared/contracts/`
once the service is wired up.

## Service shape

- FastAPI + Uvicorn (Python 3.12)
- `pyassimp` for the Assimp bindings
- `Pillow` + `imageio` for texture format conversion if a texture arrives in
  a format Rhino can't import (e.g. `.exr`, raw HDR)
- `python-multipart` for the upload endpoint

### Endpoints

| Method | Path                | Purpose                                                              |
| ------ | ------------------- | -------------------------------------------------------------------- |
| GET    | `/health`           | liveness probe (used by docker-compose healthcheck)                  |
| GET    | `/v1/formats`       | list of supported import extensions (from Assimp + our allowlist)    |
| POST   | `/v1/preconvert`    | multipart upload of source file (+ optional sidecar zip with textures); returns `<job>.zip` either as stream or `{ url }` to a server-local path |

`POST /v1/preconvert` body (multipart):
- `file` (required): the source 3D file
- `bundle` (optional): a `.zip` with sibling textures for the source file
- `flatten_hierarchy` (optional, default `false`): emit a single OBJ group
  instead of one-per-leaf
- `target_unit` (optional, default `m`): scale unit applied before emission
  (`mm` / `cm` / `m` / `inch` / `ft`)

Response:
```json
{
  "ok": true,
  "outputs": {
    "obj": "/work/<job>/model.obj",
    "mtl": "/work/<job>/model.mtl",
    "zip": "/work/<job>/model.zip"
  },
  "stats": {
    "meshes": 17,
    "vertices": 482314,
    "triangles": 161438,
    "materials": 8,
    "textures": 12,
    "leaves": 41
  },
  "manifest": {
    "layers": ["Root/Body", "Root/Body/Wheels/FrontLeft", "..." ],
    "materials": [ { "name": "PaintRed", "diffuse": [0.8, 0.1, 0.1], "map_kd": "textures/paint_red.png" } ]
  }
}
```

## Repo layout

```
PRISM/assimp/
  Dockerfile                    Linux base + libassimp + python deps
  requirements.txt              fastapi, uvicorn[standard], pyassimp, pillow, python-multipart
  src/assimp_service/
    __init__.py
    main.py                     FastAPI app, route wiring
    converter.py                Assimp scene load + node-tree walk + OBJ emission
    materials.py                aiMaterial -> MTL + texture extraction
    layers.py                   aiNode -> OBJ group path normalisation
    packaging.py                zip builder + manifest writer
  tests/
    test_smoke.py               smoke test against a tiny built-in cube.glb
  docs/
    INTEGRATION.md              server-side dispatcher contract
```

## Server-side integration

Implemented in:

- `PRISM/server/src/conversion/preconvert.ts` — `maybePreconvert` dispatcher
  + `ASSIMP_EXTS` allowlist + `isPreconvertEnabled()` toggle.
- `PRISM/server/src/api/convert.ts` — extends `SUPPORTED_EXTS` with
  `ASSIMP_EXTS`, calls `maybePreconvert` after the upload lands and before
  the BullMQ enqueue, captures `originalFormat` / `originalFileName` /
  `durationMs` into `jobs.options.preconvert` so the admin UI can still
  surface "the user uploaded a .glb".
- `PRISM/server/src/conversion/pipelines.ts` — adds an optional
  `preconvert` stage between `validate` and `queue` so the admin Pipeline
  view renders it.
- `PRISM/infra/docker-compose.yml` — `prism-assimp` service (pulls
  `ghcr.io/rebus-orbit/prism-assimp:${PRISM_ASSIMP_TAG:-latest}`,
  in-network only, healthchecked) + `ASSIMP_SERVICE_URL` env on
  `prism-server`.
- `PRISM/infra/.env.example` — `PRISM_ASSIMP_TAG` and (commented)
  `ASSIMP_SERVICE_URL` placeholders.
- `.github/workflows/assimp.yml` — builds + pushes
  `ghcr.io/rebus-orbit/prism-assimp` on changes under `assimp/**`,
  mirroring the server-image workflow, then deploys via the LAN-local
  self-hosted runner.

The agent contract (`shared/contracts/agent-protocol.{ts,cs,json}`) is
unchanged: agents only ever see `.zip` for these uploads, because Assimp
pre-conversion is fully server-side.

## Status

**Implemented.**  The Python service builds, the FastAPI surface is wired
to a working `pyassimp.load + node-walk + MTL/OBJ/manifest emit + zip`
pipeline, and the orchestrator routes Assimp formats through it before
the agent sees them.  See `docs/INTEGRATION.md` for the live HTTP
contract and `tests/test_smoke.py` for the API-surface checks.  Phase 2
work (Assimp `aiProcess_PreTransformVertices` audit on FBX, embedded-
texture format coverage for raw RGB16F EXR, layer picker UI) is tracked
in the project todo list.
