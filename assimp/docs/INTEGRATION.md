# prism-server <-> prism-assimp integration

This document captures the **contract** between the PRISM orchestrator and
the `prism-assimp` sidecar so the two can be implemented and tested
independently.

## Service discovery

`prism-server` reads `ASSIMP_SERVICE_URL` from its environment (e.g.
`http://prism-assimp:8088` when both run in the same docker-compose
network).  If the env var is unset, the orchestrator falls back to the
existing "agent-only" dispatch path (no preconvert).  This makes the
feature opt-in per deploy.

## Dispatcher logic (server-side)

Implemented in `PRISM/server/src/conversion/preconvert.ts`:

```ts
export const ASSIMP_EXTS = new Set([
  '.gltf', '.glb', '.dae', '.blend', '.x', '.usdz',
]);
// .stl and .ply are deliberately NOT here -- Rhino opens them natively
// and we'd just be adding a hop for no gain.

export async function maybePreconvert(input: MaybePreconvertInput): Promise<PreconvertOutcome | null>;
```

`/api/convert/async` calls `maybePreconvert` immediately after the upload
lands on disk and before the `jobs` row is inserted.  When pre-conversion
runs, the original upload is unlinked, a fresh `<uuid>.zip` is written to
`UPLOAD_DIR`, and the job is inserted with `format='.zip'` and
`fileName='<originalBase>.zip'`.  Pre-convert metadata is stored on
`jobs.options.preconvert`:

```json
{
  "preconvert": {
    "sidecar": "prism-assimp",
    "originalFormat": ".glb",
    "originalFileName": "scene.glb",
    "durationMs": 1843
  }
}
```

The agent dispatch path is unchanged from there: it pulls the `.zip`
URL, hands it to `ZipBundleExtractor`, and Rhino opens the resulting
OBJ as if the user had uploaded the bundle by hand.

## HTTP contract

`POST /v1/preconvert` (multipart):

| field             | type    | required | notes                                          |
| ----------------- | ------- | -------- | ---------------------------------------------- |
| file              | file    | yes      | source 3D file                                 |
| bundle            | file    | no       | optional `.zip` with sibling textures          |
| flatten_hierarchy | string  | no       | `"true"` / `"false"`                           |
| target_unit       | string  | no       | one of `mm`, `cm`, `m`, `inch`, `ft`           |
| return_mode       | string  | no       | `"json"` (default) or `"stream"`               |

Responses:

| status | meaning                                                              |
| ------ | -------------------------------------------------------------------- |
| 200    | OK; body is JSON `{ ok, job_id, outputs, stats, manifest }` or zip   |
| 400    | bad parameter (e.g. unknown `target_unit`)                           |
| 415    | unsupported file extension                                           |
| 501    | extension is allowlisted but converter not yet implemented           |
| 500    | unexpected failure (Assimp crash, disk full, …)                      |

## Manifest schema (returned in the JSON body)

```json
{
  "layers": [
    "Root/Body",
    "Root/Body/Wheels/FrontLeft",
    "Root/Body/Wheels/FrontRight",
    "Root/Trim"
  ],
  "materials": [
    {
      "name": "PaintRed",
      "diffuse": [0.8, 0.1, 0.1],
      "opacity": 1.0,
      "roughness": 0.35,
      "metalness": 0.0,
      "map_kd": "textures/paint_red.png"
    }
  ]
}
```

Layer paths use `/` as the separator.  The admin UI splits on `/` to
render a tree of checkboxes; selected paths are forwarded to the agent
via the existing `select_layers` mechanism so the user can pick which
groups end up in Rhino.

## Deployment

`infra/docker-compose.yml` gains a service block:

```yaml
prism-assimp:
  image: ghcr.io/rebus-orbit/prism-assimp:${PRISM_ASSIMP_TAG:-latest}
  container_name: prism-assimp
  restart: unless-stopped
  environment:
    LOG_LEVEL: ${LOG_LEVEL:-info}
    ASSIMP_WORK_ROOT: /work
  volumes:
    - prism-assimp-work:/work
  healthcheck:
    test: ["CMD", "curl", "-sf", "http://localhost:8088/health"]
    interval: 30s
    timeout: 5s
    retries: 3
```

`prism-server`'s service block gains:

```yaml
environment:
  ASSIMP_SERVICE_URL: "http://prism-assimp:8088"
```

A CI workflow (`.github/workflows/assimp.yml`) builds and pushes
`ghcr.io/rebus-orbit/prism-assimp` on changes under `assimp/**`,
mirroring the existing `server-image` workflow, then deploys via the
LAN-local self-hosted runner (LXC 261, 10.0.200.61) with
`ssh prism-prod 'cd /opt/prism && docker compose pull prism-assimp && docker compose up -d prism-assimp'`.

## Out of scope (Phase 1)

- Streaming Assimp output to the client (memory pressure on big scenes).
- Caching by content hash (the existing job_store dedup happens at the
  agent layer, not here).
- GPU-accelerated Assimp post-processing (none of our target formats
  benefit).
- Direct write-to-3dm bypass (Rhino's native 3DM writer is the canonical
  agent path; we are deliberately NOT building a parallel one in Python).
