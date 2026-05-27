# PRISM v0.2.0 milestone — release strategy

This document codifies how the v0.2.0 "Visualiser is production-ready"
milestone gets cut across the three independently-versioned artifacts
PRISM ships, and how subsequent maintenance releases follow.

---

## What ships under v0.2.0?

| Artifact                              | Tag at the milestone               | Source of truth                                    | What it is                                                      |
| ------------------------------------- | ---------------------------------- | -------------------------------------------------- | --------------------------------------------------------------- |
| **PRISM Agent (.NET Windows)**        | `v0.2.0`                           | `agent/src/PRISM.Agent/PRISM.Agent.csproj`         | Tray + WS client + Visualiser slot pool. Sidecar: orchestrator. |
| **PRISM Server (Node, Docker image)** | `v0.2.0` → `ghcr.io/rebus-orbit/prism-server:v0.2.0` | `server/package.json`                              | Fastify REST + WS gateway, signalling proxy, visualiser API.    |
| **Visualiser Orchestrator (.NET)**    | `visualiser-v0.2.0` *(see below)*  | `visualiser/Directory.Build.props`                 | `prism-visualiser.exe` standalone EXE bundled into the agent.   |
| **REBUS-ORBIT/orbit-ue-template**     | `v0.1.0-ue5.7-scaffold` (NO BUMP)  | Separate repo                                      | UE 5.7 project template; v1.0.0 gated on artist work — see [Open items](#open-items). |
| **coturn (`turnserver.conf` + `docker-compose.yml`)** | n/a — config-only deploy   | `infra/turn/` (lands in Phase H)                   | TURN relay on VM 211. Deployed via runbook, not auto-tagged.    |

`v0.2.0` on the PRISM repo (`REBUS-ORBIT/prism`) is the canonical
"Visualiser GA" tag. It fires the `agent.yml` + `server.yml` workflows
which publish the agent and server image. The orchestrator tag
`visualiser-v0.2.0` fires `visualiser-msi.yml` separately.

---

## Versioning continuity — the orchestrator question

The orchestrator's feature-branch tags so far are `visualiser-v0.1.0`,
`v0.1.1`, `v0.2.0`, `v0.3.0`, `v0.4.0`, `v0.5.0` (Phase J). That makes
the next semver-continuous tag `visualiser-v0.6.0`. But the v0.2.0
milestone wants every artifact to share the `0.2.0` version number for
operator clarity.

**Decision:** ship the orchestrator at `visualiser-v0.5.0` (semver
continuation, no bump) **AND** alias it as `visualiser-v0.2.0` on the
same commit. The release-notes line is:

> The v0.5.0 orchestrator build is the v0.2.0 milestone artefact —
> see also `visualiser-v0.2.0` which points at the same commit.

The alias avoids burning a major semver bump and keeps the orchestrator's
internal version stream coherent for any pre-existing automation that
expects monotonic increases.

`Directory.Build.props` ships `Version=0.5.0`. We do **not** bump it
to `2.0.0` even though the alias tag exists. Future patches go
`visualiser-v0.5.1`, `v0.5.2`, etc. — and the next milestone (v0.3.0)
may or may not coincide with `visualiser-v0.6.0`.

---

## Sequencing

1.  **Merge order.** Follow [`VISUALISER_MERGE_ORDER.md`](VISUALISER_MERGE_ORDER.md)
    strictly — 12 PRs land in order on `main`.
2.  **Cut `v0.2.0`** at the HEAD of `main` after the final Phase K
    merge:
    ```bash
    git tag v0.2.0 -m "Visualiser GA — agent + server"
    git push origin v0.2.0
    ```
    -   `agent.yml` builds `PRISM.Agent-v0.2.0.exe` + GitHub Release.
    -   `server.yml` builds + pushes `ghcr.io/rebus-orbit/prism-server:v0.2.0`.
3.  **Cut `visualiser-v0.2.0`** (and the semver-continuous alias) at the
    same commit:
    ```bash
    git tag visualiser-v0.5.0 -m "Visualiser orchestrator — v0.5.0"
    git tag visualiser-v0.2.0 -m "Visualiser orchestrator — v0.2.0 milestone alias of v0.5.0"
    git push origin visualiser-v0.5.0 visualiser-v0.2.0
    ```
    -   `visualiser-msi.yml` (filtered on `visualiser-v*`) builds the
        orchestrator EXE and uploads it to the release.
4.  **Deploy coturn** to VM 211 per the Phase H runbook
    (`infra/turn/README.md`, lands with Phase H). Requires operator
    authorisation; not automated.
5.  **Validate end-to-end** with the artist-populated UE template — see
    [Open items](#open-items) below.

---

## CI tag filters

Each workflow filters on a specific tag pattern so the three artifacts
don't cross-fire. Verified in `.github/workflows/agent.yml`,
`server.yml`, `visualiser-msi.yml`:

| Workflow              | Tag filter (`on.push.tags`)               | What runs                                                  |
| --------------------- | ----------------------------------------- | ---------------------------------------------------------- |
| `agent.yml`           | `v[0-9]*` (excluding `visualiser-v*`)     | Build agent MSI, attach to GitHub Release.                 |
| `server.yml`          | `v[0-9]*` (excluding `visualiser-v*`)     | Build + push docker image to GHCR.                         |
| `visualiser-msi.yml`  | `visualiser-v[0-9]*`                      | Build orchestrator EXE, attach to GitHub Release.          |

The exclusion in `agent.yml` / `server.yml` was added in commit
`d27217c` ("tighten v* tag filters to exclude visualiser-* prefix").

---

## Maintenance releases (post-v0.2.0)

### Hotfix flow

A hotfix is a single-PR change merged directly to `main` (no feature
branch chain), tagged with the next patch bump on whichever artifact
needs the fix.

-   Agent-only fix → `v0.2.1` (agent + server both re-publish, even
    though only one changed — server is small and republishing is cheap).
-   Server-only fix → `v0.2.1` (same).
-   Orchestrator-only fix → `visualiser-v0.5.1` (or `visualiser-v0.2.1`
    if the operator wants the v0.2.x stream to track).

### Minor bumps

`v0.3.0` is the next milestone. Likely candidates per the plan's "Open
items" / "Risks" sections:

-   **Idempotency on `POST /streams`** — deduplicate concurrent requests
    for the same `(projectId, modelId, versionId)`.
-   **`callbackUrl` actually fires** — POST status updates to the portal
    asynchronously.
-   **Connector-side MVR/GDTF emission** — Rhino + Vectorworks connectors
    detect MVR data in source CAD and surface it as a project attachment
    automatically.

---

## Open items

### UE template milestone gating

The Visualiser milestone v0.2.0 ships with the **placeholder scaffold**
of the UE template at `REBUS-ORBIT/orbit-ue-template@v0.1.0-ue5.7-scaffold`.
The full artist-populated template (`v1.0.0-ue5.7`) is pending creative-team
work and lands as a **v0.2.1 hotfix**:

1.  Artist team commits the final `BaseLevel.umap`, `BP_OrbitImporter`,
    `M_DefaultLit`, and DMX plugin wiring to `orbit-ue-template`.
2.  Tag `v1.0.0-ue5.7` on the template repo.
3.  Bump the agent's default `UnrealTemplateTag` from
    `v0.1.0-ue5.7-scaffold` to `v1.0.0-ue5.7` in `AgentConfig.cs`.
4.  Tag PRISM `v0.2.1`.

In the meantime the visualiser surface is contract-complete — portals
can integrate against v0.2.0 today; they'll just see the scaffold's
default-grey level when streams start. The artist template lands without
a contract change.

### Code-signing

Per the plan's "Out of scope" section, code-signing the orchestrator and
agent EXEs is parked. Not blocking v0.2.0. Tracked for a future release
once Rebus Industries acquires an EV code-signing certificate.

### Observability

Server-side metrics (`/metrics` Prometheus endpoint) for visualiser run
counts, dispatch latencies, and TURN bandwidth usage are not in v0.2.0.
Phase H wires basic bandwidth monitoring via coturn's own
`telnet 127.0.0.1 5766` admin interface; PRISM-level surfacing is a
v0.3.0 candidate.

---

## See also

-   [`VISUALISER_MERGE_ORDER.md`](VISUALISER_MERGE_ORDER.md) — operator
    runbook for sequencing the 12 PRs.
-   [`PORTAL_INTEGRATION.md`](PORTAL_INTEGRATION.md) — third-party portal
    integrator guide.
-   `.github/workflows/agent.yml`, `server.yml`, `visualiser-msi.yml` —
    CI workflow definitions.
-   `CHANGELOG.md` — per-version notes; the v0.2.0 milestone entry rolls
    up every Phase A-K contribution.
