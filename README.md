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
  shared/                 cross-language contracts (JSON Schema -> TS + C# codegen)
  infra/                  docker-compose, Caddy snippet, .env.example
  .github/workflows/      CI: server image, agent .msi, deploy.yml
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

## Status

| Phase | What | State |
|---|---|---|
| 0 | Repo bootstrap, scaffold, submodule | done |
| 1 | Fastify server core, Drizzle schema, auth | done |
| 2 | WS gateway, agent protocol, agent skeleton | done |
| 3 | Rhino.Inside conversion, ORBIT upload | done |
| 4 | Admin + Convert SPAs | done |
| 5 | Vue Flow live pipeline editor | pending |
| 6 | Receive (ORBIT -> .3dm/.step), IFC/3DM/GLB outputs | pending |
| 7 | Public `/v1/*` external API | pending |
| 8 | CI + deploy to VM 211 | pending |

## Source policy

3DConvert (the legacy Python service on the Speckle prod VM) and any
`CheekiSkrub/*` repo are **read-only reference**. No legacy code is ported.
Reuse is restricted to first-party ORBIT-org code in
[`vendor/orbit-monorepo/SDK/`](vendor/orbit-monorepo) and the
`OrbitConnector.Rhino.Core` library extracted in Phase 2.
