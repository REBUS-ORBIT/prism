# PRISM

**ORBIT-native node-based conversion pipeline.**

PRISM accepts CAD / mesh / IFC files via API or web UI, dispatches conversion
work to a pool of Rhino workstation agents, and uploads the resulting ORBIT
objects directly to `orbit-server` — preserving native B-rep / SubD / Extrusion
geometry through `RhinoDataObject.rawEncoding`.

This repo is part of the [REBUS-ORBIT](https://github.com/REBUS-ORBIT) org and
is independent of the ORBIT monorepo (`REBUS-ORBIT/orbit-server`). It pulls
in the ORBIT SDK + Rhino-connector core library as a **git submodule** at
[`vendor/orbit-monorepo/`](vendor/orbit-monorepo).

## Layout

```text
prism/
  vendor/orbit-monorepo/  submodule -> REBUS-ORBIT/orbit-server @ pinned commit
  server/                 TypeScript orchestrator (Fastify + WS)
  web/                    Vue 3 admin + convert SPAs
  agent/                  C# .NET 8 Windows service (Rhino.Inside)
  visualiser/             C# orchestrator for the Visualiser role (UE 5.7 + Pixel Streaming)
  shared/                 cross-language contracts (JSON Schema -> TS + C# codegen)
  infra/                  docker-compose, Caddy snippet, .env.example
  docs/                   PORTAL_INTEGRATION.md, RELEASE_STRATEGY.md, ANTIVIRUS_EXCLUSIONS.md, ...
  .github/workflows/      CI: server image, agent .msi, visualiser .exe, deploy.yml
```

## Two-layer architecture

```text
                       prism.rebus.industries (Caddy)
                                   |
+--------------------------- PRISM Server (VM 211) ---------------------------+
|  Fastify REST + WS gateway                                                  |
|  Postgres (jobs/keys/settings/workstations/presets)  +  Redis (BullMQ)      |
|  Vue 3 admin SPA (with live flow editor)  +  public Convert SPA             |
+-----------------------------------------------------------------------------+
            ^                                              |
            | WSS (register / pull job / progress / log)   | ORBIT object upload
            |                                              v
+--- PRISM.Agent.exe (Windows) ---+              +--- orbit-server ---+
|  Rhino.Inside .NET 8            |              |  (VM 211)          |
|  RhinoFileOpener (all formats)  |              +--------------------+
|  OrbitConnector.Rhino.Core      |
|  N concurrent worker slots      |
+---------------------------------+
```

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the full design,
[`DEPLOY.md`](DEPLOY.md) for the server deployment runbook, and
[`AGENT_INSTALL.md`](AGENT_INSTALL.md) for the workstation install.

## Visualiser

PRISM Visualiser is the second layer on top of the conversion fleet —
it lets a third-party portal embed a live, interactive 3D view of any
ORBIT project, rendered on a REBUS workstation in Unreal Engine 5.7 and
streamed over WebRTC. Production-ready as of `v0.2.0`.

The portal calls one endpoint
(`POST /api/visualiser/streams`), waits ~2-3 s (warm) / ~60-90 s (cold),
and gets back a WebRTC signalling URL + TURN credentials. The browser
embeds Epic's Pixel Streaming frontend and the user is interactively
flying around the model.

```text
  Portal browser <--WSS--> prism.rebus.industries <----> PRISM.Agent.exe ---> prism-visualiser.exe ---> UnrealEditor.exe (-game, Pixel Streaming 2)
                                                                                                                |
                                                                                            visualiser.rebus.industries (coturn)
                                                                                              |
                                                                                              +-- WebRTC media relay
```

Third-party integrators: see [`docs/PORTAL_INTEGRATION.md`](docs/PORTAL_INTEGRATION.md)
(narrative, ~600 lines, code samples in React/Vue/vanilla JS/Python/curl)
and [`https://prism.rebus.industries/docs`](https://prism.rebus.industries/docs)
(machine-readable OpenAPI 3.1).

Operators: see [`visualiser/README.md`](visualiser/README.md) for the
orchestrator architecture, [`docs/ANTIVIRUS_EXCLUSIONS.md`](docs/ANTIVIRUS_EXCLUSIONS.md)
for workstation tuning, and [`docs/RELEASE_STRATEGY.md`](docs/RELEASE_STRATEGY.md)
for the v0.2.0 milestone runbook.

## Status

| Phase | What | State |
|---|---|---|
| 0 | Repo bootstrap, scaffold, submodule | done |
| 1 | Fastify server core, Drizzle schema, auth | done |
| 2 | WS gateway, agent protocol, agent skeleton | done |
| 3 | Rhino.Inside conversion, ORBIT upload | done |
| 4 | Admin + Convert SPAs | done |
| 5 | Vue Flow live pipeline editor | done |
| 6 | Receive (ORBIT -> .3dm/.step), IFC/3DM/GLB outputs | done |
| 7 | Public `/v1/*` external API | done |
| 8 | CI + deploy to VM 211 | done |
| 9 | Visualiser (UE 5.7 + Pixel Streaming portal contract) | done (v0.2.0) |

## Live deployment

| Surface | URL | Backing |
|---|---|---|
| Public UI (admin) | https://prism.rebus.industries/admin/ | `prism-server` on VM 211 |
| Public UI (convert) | https://prism.rebus.industries/convert/ | same |
| Public API | https://prism.rebus.industries/api/* and `/v1/*` | same |
| Health | https://prism.rebus.industries/health | same |

Server stack lives at `/opt/prism/` on VM 211 (`10.0.200.211`), runs three
containers (`prism-server`, `prism-postgres`, `prism-redis`) and is fronted by
the existing HA Caddy pair (LXC 251 / 252) — block already in `/etc/caddy/Caddyfile`.

## Source policy

3DConvert (the legacy Python service on the Speckle prod VM) and any
`CheekiSkrub/*` repo are **read-only reference**. No legacy code is ported.
Reuse is restricted to first-party ORBIT-org code in
[`vendor/orbit-monorepo/SDK/`](vendor/orbit-monorepo) and the
`OrbitConnector.Rhino.Core` library extracted in Phase 2.
