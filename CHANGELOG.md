# Changelog

All notable changes to **PRISM** (server + agent + connector submodule) live
here. Versions tagged on this repo are agent versions; server image tags follow
the same numbering when bumped, otherwise server ships as rolling deploys off
`main` via the `server-image` workflow.

The format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Release-notes convention

Each `## vX.Y.Z` section below is auto-extracted into the matching GitHub
Release body on tag push (see `.github/workflows/agent.yml`, step *Extract
release notes from CHANGELOG*). Use the format:

    ## vX.Y.Z — Optional title

    - Bullet describing change 1
    - Bullet describing change 2

The CI extracts everything between `## vX.Y.Z` and the next `## v` heading,
verbatim, so subsections (`### Fixed`, `### Added`, blockquotes, etc.) come
through unchanged. Lines preceding the first `## v` header (including the
`## Unreleased` working area) are ignored by the extractor.

---

## Unreleased

### Changed

- **Workstations admin + pipeline "Open Web UI" links now use the
  agent's connected IP** (from `agent_sessions.remote_addr`) instead of
  `nodeName.dnsSuffix`. Chrome's HTTPS-First Mode (and any HSTS
  `includeSubDomains` policy on `rebus.industries`) silently upgrades
  `http://<name>.rebus.industries:7421/` to `https://`, which the
  agent's plain-HTTP listener doesn't serve, so every click hit an SSL
  error. Bare IPs are exempt from Chrome's HTTPS-upgrade logic, so
  switching to the agent's live IP fixes the link immediately and also
  makes the feature work on flat LANs that don't have AD DNS to
  resolve the suffix. Falls back to the legacy `nodeName.dnsSuffix`
  URL when the agent is offline (no live IP to surface).

### Added

- **Server — Fastify `trustProxy: true`** (`server/src/main.ts`): without
  this, `req.ip` returns the immediate TCP peer (the external Caddy LXC,
  `10.0.200.251`) instead of honouring `X-Forwarded-For`, so every
  `agent_sessions.remote_addr` row landed pointing at the proxy. Safe
  to enable unconditionally — prism-server is only reachable from the
  proxy pair or other hosts on the private `10.0.200.0/24` VLAN.
- **Server — `host` field on `/api/workstations`** (`server/src/api/workstations.ts`):
  list + get responses now include `host`, populated from the most
  recently active `agent_sessions.remote_addr` for that workstation
  (preferring the row with the freshest heartbeat). Returns `null`
  when no agent session exists.
- **Server — IP normalisation on WS hello**
  (`server/src/ws/agentProtocol.ts`): the captured peer address is now
  stripped of any `::ffff:` IPv4-mapped IPv6 prefix before being
  persisted into `agent_sessions.remote_addr`, so dual-stack listeners
  produce the bare IPv4 form everyone expects (`10.0.10.202`, not
  `::ffff:10.0.10.202`).
- **Web — `Workstation.host` typed client field** (`web/src/shared/api.ts`):
  surfaces the new server field to all consumers.
- **Web — `workstationWebUiHost` / `workstationWebUiUrl` accept an
  optional `host` parameter** (`web/src/shared/workstationUrl.ts`).
  New precedence: live IP > `nodeName.dnsSuffix` > bare `nodeName`.
  Both call sites (`Workstations.vue` and `FlowEditor.vue`) thread
  `Workstation.host` through; FlowEditor's live-data path
  (`applyLiveData`) also pushes `host` / `webUiUrl` into Vue Flow node
  data so the link updates when an agent reconnects from a new IP.

### Notes

- No DB schema changes — `agent_sessions.remote_addr` already existed
  on the schema; we now just normalise it on insert and surface it on
  the API.
- `agent_sessions` rows are deleted on socket close, so an offline
  workstation has no historical IP to fall back to and the SPA
  reverts to the `nodeName.dnsSuffix` legacy path. Once the agent
  reconnects after this server deploy, the live IP populates within
  one `hello` round-trip.

---

## v0.1.38 — 2026-05-27 — Visualiser Phase G: server API + WS signalling proxy + admin UI

> **Phase G of the Visualiser feature.** Wires up the portal-facing
> `/api/visualiser/streams` REST surface, the bidirectional WS signalling
> proxy that fronts the orchestrator's local Cirrus, the admin UI
> start/stop pages, and the API-key scope guard. The end state per the
> plan: PRISM admin UI (or `curl`) can start a stream, get a signalling
> URL, and a co-located browser can connect.
>
> Co-released with `v0.1.38` of the agent — the only agent-side change
> in Phase G is a new `signallingFrame` WS handler stub so the server's
> outbound envelopes have a place to land. The local-Cirrus hop on the
> agent side wires up when the orchestrator branch (Phases B-F) merges
> in; until then the agent debug-logs and drops inbound frames.

### Added

- **Shared contracts** (`AgentProtocol.cs`, `agent-protocol.ts`,
  `agent-protocol.json`): two new envelopes — `VisualisationEnded`
  (agent → server, terminal cleanup after TTL expiry / UE exit /
  browser disconnect) and `SignallingFrame` (bidirectional, either
  `payload` text or `payloadB64` base64-encoded binary). Verified by
  `npm run validate:contracts` (21 message types across all three
  representations).
- **Server REST API** — new file `server/src/api/visualiser.ts`
  registering five routes under `/api/visualiser`:
    - `POST /streams` (requires `visualiser:create_stream` scope or
      admin session) — synchronous start. Validates the body, inserts a
      `queued` row, dispatches `startVisualisation` to the least-loaded
      eligible workstation via `tryDispatchVisualisation`, awaits the
      agent's `visualisationReady` / `visualisationFailed` reply through
      a per-runId Promise registry (timeout configurable via
      `VISUALISER_START_TIMEOUT_MS`, default 180s). Returns the plan's
      `prism-visualiser/ready/v1` shape — `runId`, `signallingUrl`,
      `playerUrl`, `streamerId`, `turn: { urls, username, credential,
      ttl } | null` — on success; the `failed/v1` shape with a
      machine-readable `code` (`start_timeout`, `agent_failed`,
      `no_workstation_available`, `all_workstations_busy`,
      `misconfigured`, `agent_send_failed`) on failure.
    - `GET  /streams` and `GET /streams/:runId` for polling (admin
      auth).
    - `DELETE /streams/:runId` (caller must be the API key that
      started the run, or admin) — sends best-effort
      `cancelVisualisation`, transitions row to `ended`, releases the
      workstation slot.
    - `POST /streams/:runId/signalling-token` — mints a 5-minute HS256
      JWT scoped to one runId. Returns 503 with a clear error when
      `JWT_SIGNALLING_SECRET` is unset rather than minting an unverifiable
      token.
    - `GET  /workstations` (admin only) — list of eligible visualiser
      workstations + their current load, for the start-stream
      dropdown.
- **Server WS signalling proxy** —
  `server/src/ws/signallingProxy.ts` mounts a websocket route at
  `/ws/visualiser/:runId/signalling?token=<jwt>`. Verifies the JWT
  against `expectedRunId`, refuses non-`streaming` runs (4409),
  refuses runs with no agent session (4503), then registers the
  browser socket in an in-process registry and forwards every frame
  to the agent via `sendSignallingFrameToAgent`. Inbound
  `signallingFrame`s from the agent fan out to every browser socket
  for that runId. Binary frames are base64-wrapped into
  `payloadB64`; text frames go through as `payload`. PRISM does not
  parse the Pixel Streaming sub-protocol — the wrappers are opaque
  envelopes. The registry is extracted into
  `signallingProxyRegistry.ts` to break the import cycle with
  `agentProtocol.ts`.
- **Server WS inbound handlers** (`server/src/ws/agentProtocol.ts`):
  Phase A's stub `visualisationReady` / `visualisationFailed`
  handlers now actually update the `visualiser_runs` row, fire the
  Promise waiter in `visualiserRunRegistry`, decrement the
  workstation's visualiser-load counter via `releaseVisualiserSlot`,
  close any browser proxy connections for the run, and broadcast a
  `workstation_updated` event to admin SSE. A new
  `visualisationEnded` handler runs the same cleanup path (terminal
  state, no waiter to fire). The `signallingFrame` handler dispatches
  to `signallingProxyRegistry.forwardAgentToBrowser`.
- **Server TURN credential generator** —
  `server/src/visualiser/turnCredentials.ts` implements RFC 7635 §3
  long-term credentials (`base64(HMAC-SHA1(TURN_SECRET, "<exp>:<tag>"))`
  with `exp = now + ttlSeconds`). Returns `null` sentinel when
  `TURN_SECRET` is unset so the portal can still receive the rest of
  the ready response — Phase H wires the real coturn deploy. Honours
  `TURN_REALM` (default `visualiser.rebus.industries`) and
  `TURN_URLS_OVERRIDE` for staging.
- **Server signalling token issuer** —
  `server/src/visualiser/signallingToken.ts` implements hand-rolled
  HS256 JWT issue/verify against `JWT_SIGNALLING_SECRET`. Each token
  carries `runId` + 5-minute `exp`; verify enforces signature,
  expiry, and an `expectedRunId` match so a leaked token can't be
  replayed against a different run.
- **Server run registry** — `server/src/visualiser/runRegistry.ts`,
  the per-runId Promise map that bridges the synchronous POST
  request to the agent's async WS reply. Supports timeout, supersede,
  and abandon.
- **Server dispatcher hardening** (`server/src/jobs/dispatcher.ts`):
  `tryDispatchVisualisation` now returns a discriminated outcome
  (`no_workstation_available` / `all_workstations_busy` /
  `agent_send_failed` / `misconfigured` / `invalid_state` / success),
  picks the least-loaded `can_visualise = true` workstation, and
  reserves a slot atomically via `UPDATE workstations SET
  current_visualiser_load = current_visualiser_load + 1 WHERE id = ?
  AND current_visualiser_load < slots RETURNING …`. The
  optimistic-update pattern means concurrent dispatchers race
  cleanly: the loser sees zero rows and tries the next candidate.
  Rollback on agent ws send failure clamps the counter at zero via
  `GREATEST(current_visualiser_load - 1, 0)`. New
  `releaseVisualiserSlot(workstationId)` is called from every
  terminal-state agent envelope and from the DELETE endpoint.
- **Database** (`schema.ts` → `0004_visualiser_phase_g.sql`):
  `workstations.current_visualiser_load int NOT NULL DEFAULT 0`,
  `visualiser_runs.player_url text`,
  `visualiser_runs.failure_reason varchar(64)`, and
  `visualiser_runs.requested_by_api_key_id uuid` with `FK → api_keys.id
  ON DELETE SET NULL`.
- **OpenAPI** (`server/src/docs/openapi.ts`): documents all five
  `/api/visualiser/*` paths with request / response schemas, the
  `X-API-Key` scope requirement, and request + response examples for
  the start-stream happy path. Adds a second `servers[]` entry so the
  rendered spec correctly resolves `/api/visualiser/*` paths against
  the deployment root rather than under `/v1`. Phase K writes the
  narrative companion docs.
- **Web admin UI**:
    - `web/src/admin/pages/Visualiser.vue` — table of recent runs,
      live duration ticker on streaming rows, ORBIT project-name
      resolution (cached client-side), per-row Stop + Open viewer
      action buttons, and a Start-stream modal that reuses
      `OrbitPicker` for project/model selection and calls
      `GET /api/visualiser/workstations` for the (optional)
      workstation dropdown. Polls every 5s while any non-terminal
      run exists; stops automatically when everything settles.
    - `web/src/admin/pages/VisualiserViewer.vue` — minimal `<iframe>`
      shim pointing at the orchestrator's `playerUrl` with a Loading…
      overlay and per-status placeholder copy. Phase I replaces this
      with a real Pixel Streaming embed driven by the new signalling
      WS proxy.
    - `web/src/shared/api.ts` — new `visualiserApi` client with
      `listStreams` / `getStream` / `startStream` / `stopStream` /
      `listWorkstations` / `signallingToken`, plus typed
      `VisualiserRun`, `VisualiserReadyEvent`, `VisualiserTurnBundle`,
      `VisualiserWorkstation`, `VisualiserStartBody`,
      `VisualiserStatus` interfaces.
    - `web/src/admin/App.vue` + `web/src/admin/main.ts` — new
      "Visualiser" sidebar entry (between Pipeline and API keys) and
      routes `/visualiser` + `/visualiser/:runId`.

### Agent (v0.1.38)

- **AgentMessageDispatcher**: new `MessageType.SignallingFrame`
  branch wired up. The handler is a Phase G stub — the orchestrator-
  side bridge that forwards to local Cirrus lands when the
  orchestrator branch merges in. Until then the agent logs at debug
  and drops the frame, so the server-side proxy stays connected for
  the browser's lifetime without raising.

### Server: testing

Server gains its first test files (4 suites, 35 passing): unit tests
for the TURN credential generator (RFC 7635 format, TTL handling,
sentinel behaviour, realm + URL override env vars), the HS256
signalling token (issue / verify round-trip, replay protection on
mismatched `expectedRunId`, expired-token rejection, signature
forgery rejection), the in-memory run registry (resolve / reject /
timeout / supersede / abandon), and the visualiser dispatcher
(selection logic, race-loss roll-forward, atomic reservation
rollback on agent send failure, misconfiguration rollback). The
dispatcher suite mocks the `db` client at module boundary so it
runs in-process with no Postgres dependency.

### Deviations from spec

- The signalling-token route returns 503 (rather than 500) when
  `JWT_SIGNALLING_SECRET` is unset so operators see a clear
  "misconfigured, can't mint a token" signal rather than a generic
  server error — same shape as the TURN sentinel.
- The admin Visualiser nav entry sits between Pipeline and API keys
  rather than between Pipeline and Workstations as the spec
  suggested — that placement keeps the role-management surfaces
  (Workstations, API keys) adjacent in the sidebar and matches the
  current "Workstations → role pills → Visualiser stream consumer"
  reading order.
- The `signallingFrame` envelope carries either `payload` (string)
  or `payloadB64` (base64-encoded binary), with exactly one set per
  frame. This is slightly more explicit than the spec's `raw
  binary/text payload` phrasing — Newtonsoft.Json round-trips a
  byte[] field through base64 by default but the explicit field
  split lets the JSON Schema validator catch malformed envelopes.
- The full `POST /api/visualiser/streams` integration test (Fastify
  inject + mocked WS gateway) was deferred — the suite would need a
  full Postgres fixture which the server doesn't yet have any
  integration-test infrastructure for. The dispatcher unit tests
  cover the same selection / reservation surface; the API route's
  thin wrapping over them is exercised by `tsc --noEmit` and the
  contract validator.

### Pending follow-ups

- **Phase H** wires `TURN_SECRET` + matching coturn deployment. The
  sentinel `turn: null` already round-trips through the API and the
  admin SPA.
- **Phase I** replaces `VisualiserViewer.vue`'s iframe with a real
  Pixel Streaming embed and lands the orchestrator-side bridge in
  the agent's `signallingFrame` handler.
- **Phase K** writes the narrative portal docs against the
  machine-readable OpenAPI spec wired up here.

---

## v0.1.37 — 2026-05-27 — Visualiser role plumbing (no orchestrator yet)

> **Phase A of the Visualiser feature.** This release lands the *plumbing* —
> role flag, settings storage, contracts, DB schema, dispatcher branch,
> and admin/agent UI — but **no orchestrator binary, no Unreal Engine
> integration, and no signalling proxy**. The agent's `startVisualisation`
> WS handler intentionally acks `accepted: false` with reason
> `"visualiser orchestrator not yet implemented"`. Wiring to a real
> `VisualiserSession` lands in Phase F/G.

### Added

- **Shared contracts** (`shared/contracts/AgentProtocol.cs`,
  `agent-protocol.ts`, `agent-protocol.json`): `Visualiser` added to the
  `AgentRole` enum. Four new `MessageType`s — `startVisualisation`,
  `cancelVisualisation` (server → agent), `visualisationReady`,
  `visualisationFailed` (agent → server) — with matching `*Data`
  payload records covering `runId`, ORBIT credentials, project / model /
  version ids, template tag, signalling URL, stream id, expiry, and
  error fields. Verified by `npm run validate:contracts` (19 message
  types across all three representations).
- **Database schema** (`server/src/db/schema.ts` →
  `0003_visualiser.sql`): `workstations.can_visualise boolean DEFAULT
  false`, `api_keys.scopes jsonb DEFAULT '[]'::jsonb`, and a new
  `visualiser_runs` table keyed by `status varchar(16)` enum (`queued |
  importing | streaming | failed | ended`) with FK back to
  `workstations`. Indexes on `status`, `created_at`, and `project_id`.
- **Server — `tryDispatchVisualisation(runId, log)`**
  (`server/src/jobs/dispatcher.ts`): new exported function that picks
  an eligible agent purely by `workstation.is_enabled +
  workstation.can_visualise + slots_busy < slots` (no
  `supportedFormats` check — the visualiser stream is format-agnostic
  at this layer), sends the `startVisualisation` envelope over the
  existing agent WS session, and transitions the row to `importing`.
  No API caller wired yet — that's Phase G.
- **Agent config** (`agent/src/PRISM.Agent/Config/AgentConfig.cs`): four
  new fields persisted via the existing JSON write path —
  `UnrealEngineRoot` (default `C:\Program Files\Epic Games\UE_5.7\`),
  `UnrealTemplateTag` (default `v1.0.0-ue5.7`), `VisualiserMaxConcurrent`
  (default `1`), `VisualiserGpuCheck` (default `true`). `AgentControlPlane`
  applies them live (no agent restart required).
- **Agent web UI** (`agent/src/PRISM.Agent/WebUi/IndexHtml.cs`): new
  *Visualiser* card on the settings page rendered only when the
  `visualiser` role checkbox is on; binds to the four new config fields
  via `POST /api/config`. Matches the existing card styling and respects
  the light/dark CSS variables.
- **Tray SettingsForm** (`agent/src/PRISM.Agent/Tray/SettingsForm.cs`):
  matching *Visualiser* group box with the same four controls. Form
  border style raised to `Sizable` so the operator can resize past the
  default footprint when the role expands.
- **Agent WS dispatcher** (`agent/src/PRISM.Agent/Ws/AgentMessageDispatcher.cs`):
  `startVisualisation` and `cancelVisualisation` cases land, log a
  clear `WARN`, and ack with `Accepted = false, Reason = "visualiser
  orchestrator not yet implemented"`. These intentionally do nothing
  with the payload beyond logging — Phase F/G will replace this stub
  with a real `VisualiserSession` handoff and the reverse-channel
  `visualisationReady` / `visualisationFailed` envelopes.
- **Agent startup validation** (`agent/src/PRISM.Agent/AgentService.cs`):
  when `Visualiser` is in `Config.Roles`, the service now checks
  `Directory.Exists(Config.UnrealEngineRoot)` on startup and emits a
  loud `Log.Warning` (`Visualiser role enabled but UE root not found:
  ...`) if it's missing. The agent continues running so the other
  roles still work — the dispatcher filters this box out via
  `can_visualise` until the admin corrects the config.
- **Admin UI — `can_visualise` role pill**
  (`web/src/admin/pages/Workstations.vue`,
  `web/src/shared/api.ts`,
  `server/src/api/workstations.ts`): new toggle alongside
  `convert / layer / receive`, hits `PATCH /api/workstations/:id`
  with `canVisualise: boolean`. The pill uses the ORBIT primary
  fade token to stay visually distinct.
- **API key scopes**
  (`server/src/db/schema.ts`,
  `server/src/auth/{apiKey,principal,middleware}.ts`,
  `server/src/api/keys.ts`,
  `web/src/admin/pages/ApiKeys.vue`,
  `web/src/shared/api.ts`): `api_keys.scopes jsonb` is read into the
  request principal at auth time; new `requireScope(scope)` Fastify
  guard returns 403 unless the principal is an admin/ORBIT bearer or
  an API key with the scope present. `GET /api/keys/scopes` returns
  the canonical scope catalog (`visualiser:create_stream` for Phase
  A). The admin UI renders these as checkboxes on the create form
  plus an *Edit scopes* modal per row. Pre-Phase-A keys keep an empty
  list and explicitly do *not* inherit new scopes.

### Notes

- WS handlers return `accepted: false` until the orchestrator binary
  lands in Phase F. The whole release is *plumbing only* — admins can
  toggle workstations into the visualiser pool and configure UE
  settings on each agent, but the next dispatcher hop will hit the
  stub above and refuse the run cleanly.

---

## v0.1.36 — 2026-05-27 — Updater hotfix

> **Recovery note for v0.1.34 / v0.1.35 users:** the existing in-app
> updater **cannot** install v0.1.36 because of the same
> `ExtractToDirectory` bug it is meant to fix. You must **manually
> download** `PRISM.Agent-Setup-v0.1.36.exe` from
> [GitHub Releases](https://github.com/REBUS-ORBIT/prism-agent/releases/tag/v0.1.36)
> and run it. The installer cleanly replaces the running agent. After
> v0.1.36 is installed, all future in-app updates (tray "Check for
> Updates" and remote WS `update` requests) will work.

### Fixed

- **Critical: PowerShell extract call crashed on Windows PowerShell 5.1.**
  The updater script embedded in `agent/src/PRISM.Agent/Tray/Updater.cs`
  called
  `[IO.Compression.ZipFile]::ExtractToDirectory($zip, $installDir, $true)`,
  intending `$true` as the `overwriteFiles` argument. That 3-arg
  `(string, string, bool)` overload only exists on .NET Core 3.0+. The
  default `powershell.exe` (Windows PowerShell 5.1 / .NET Framework 4.x)
  loads the older `System.IO.Compression.FileSystem.dll`, which only
  has `(string, string, Encoding)`. PowerShell's method binder tried to
  coerce `$true` → `System.Text.Encoding` and threw immediately
  (`Cannot convert value "True" to type "System.Text.Encoding"`). The
  agent then quietly relaunched the OLD binary, which on every "Update"
  click landed back in the same broken updater. **No v0.1.34 or v0.1.35
  in-app update has ever actually extracted anything.**
  Replaced with `Expand-Archive -LiteralPath $zip -DestinationPath
  $installDir -Force -ErrorAction Stop`, which has been overwrite-aware
  since PowerShell 5.0 and ships with every supported Windows.
- **Post-extract verification before relaunch.** The PS helper now
  `Test-Path`s `PRISM.Agent.exe` after extraction and reads its
  `ProductVersion` into the log so the operator can see the new version
  stamp before the relaunch line. If the EXE is missing, the script
  marks `$fatal = $true` and pauses the visible window so the user gets
  a real error message instead of having the old agent silently
  relaunched (and the next "Update" click landing in the same loop).

### Added

- **Concurrent-update guard** (`Updater.cs`): process-wide
  `SemaphoreSlim _updateGate = new(1, 1)` wraps the body of
  `DownloadAndInstallAsync`. `WaitAsync(0)` fails fast with
  `InvalidOperationException("Another update is already in progress on
  this agent.")` instead of queueing. Stops a remote (WS) and a local
  (tray "Check for Updates") update from racing on the same temp zip
  and install dir — a scenario that may have contributed to the
  "file is being used by another process" report on top of the primary
  `ExtractToDirectory` crash.
- **`Updater.IsUpdateInProgress`** public read-only probe so the tray
  menu and the WS dispatcher can short-circuit BEFORE touching GitHub
  Releases when an update is already running.
- **`UpdateOutcome.AlreadyRunning`** flag on
  `AgentControlPlane.CheckAndApplyUpdateAsync`. The agent's local
  HTTP listener (`AgentWebUi`) now returns **HTTP 409 Conflict** with
  `{ ok: false, alreadyRunning: true }` instead of the generic 502, so
  the server / admin UI can surface a "wait, then retry" message.
- **WS dispatcher** (`AgentMessageDispatcher.HandleUpdate`) now
  inspects the `UpdateOutcome` and logs `WARN` (not `ERROR`) on
  `alreadyRunning`, so a benign collision doesn't look like a real
  update failure in the agent log pipeline.
- **Tray UI** (`PrismTrayContext`): the "Check for Updates" menu
  short-circuits early when `Updater.IsUpdateInProgress` is true and
  shows a friendly "An update is already in progress" info dialog
  instead of racing into the GitHub fetch. `InstallUpdateAsync` also
  catches `InvalidOperationException` separately so a collision
  surfaces as an info dialog, not as the red `Update Error` box.

### Defensive

- **Stale-zip cleanup** at the top of `DownloadAndInstallCoreAsync`:
  any leftover `%TEMP%\PRISM.Agent.Update.zip` from a previous
  interrupted attempt is `File.Delete`d before the new download
  opens its FileStream. Removes one cause of "file is being used by
  another process" errors when antivirus or a partial-download
  handle was still pinning the stale file.
- **`FileShare.Read` on the writing FileStream**
  (`new FileStream(tempZip, FileMode.Create, FileAccess.Write,
  FileShare.Read)`): antivirus / Defender can stream-scan the partial
  zip without producing a sharing-violation against our write.
- **Tighter `await using` scope** around the network + filesystem
  handles so they're disposed immediately after the download loop
  ends rather than at method exit, well before the PowerShell helper
  is spawned. Eliminates one race-condition surface from the FATAL
  post-mortem flow.

### Files touched

- `agent/src/PRISM.Agent/Tray/Updater.cs` — the PowerShell here-string
  (extract + verification), `SemaphoreSlim` gate, stale-zip delete,
  `FileShare.Read`, scoped streams.
- `agent/src/PRISM.Agent/AgentControlPlane.cs` — `UpdateOutcome` record
  gains `AlreadyRunning`; `CheckAndApplyUpdateAsync` short-circuits on
  `Updater.IsUpdateInProgress` and catches `InvalidOperationException`
  from the background `Task.Run`.
- `agent/src/PRISM.Agent/Ws/AgentMessageDispatcher.cs` — `HandleUpdate`
  inspects the outcome and logs WARN for benign already-running races.
- `agent/src/PRISM.Agent/Tray/PrismTrayContext.cs` — `OnCheckUpdate`
  early-return + `InstallUpdateAsync` `InvalidOperationException`
  branch.
- `agent/src/PRISM.Agent/WebUi/AgentWebUi.cs` — `POST /api/agent/update`
  returns 409 with `alreadyRunning: true` when a download is in flight.
- `agent/src/PRISM.Agent/PRISM.Agent.csproj` — version bumped
  `0.1.35` → `0.1.36` (all four fields).

---

## v0.1.35 — 2026-05-27

PRISM logo branding across every agent surface a user sees: Windows
Explorer / Task Manager / taskbar entry, Alt-Tab thumbnail, system-tray
icon, the local web UI header, and the Start Menu + Desktop shortcuts
created by the wizard installer. No behavioural changes — the WS
protocol, scheduled task, updater, and Rhino pipeline are byte-for-byte
identical to v0.1.34.

### Added

- **Multi-resolution `PRISM.Agent.ico`**
  (`agent/src/PRISM.Agent/Assets/PRISM.Agent.ico`): brand-new asset
  generated from `PRISM/prism-logo.png` via `tools/make-ico.ps1`.
  Six PNG-compressed frames baked in at 16/32/48/64/128/256 px so
  every Windows shell consumer (16 px tray, 32 px window title bar,
  48 px Explorer "Large icons", 256 px "Extra large" + "Tile") picks
  up a crisp render without bilinear-stretching a single-size icon.
  Total file 78,672 bytes. Generator is pure PowerShell + `System.Drawing`
  so it runs on any Windows dev box without ImageMagick/Chocolatey.
- **Agent EXE carries the brand icon**
  (`agent/src/PRISM.Agent/PRISM.Agent.csproj`):
  `<ApplicationIcon>Assets\PRISM.Agent.ico</ApplicationIcon>` bakes the
  multi-res `.ico` into the PE resource table. Explorer, Task Manager,
  Alt-Tab, the Windows 11 taskbar, and the Inno Setup uninstall entry
  (`UninstallDisplayIcon={app}\PRISM.Agent.exe`) all auto-pick it up
  from the executable's own resources.
- **Side-by-side `Assets/` content** (csproj `<Content Include>` items
  with `CopyToOutputDirectory=PreserveNewest`): both `PRISM.Agent.ico`
  and `prism-logo.png` ship next to `PRISM.Agent.exe` in the publish
  output so the tray-icon loader, the web UI's data-URL substitution,
  and the installer's shortcut `IconFilename:` parameter can all read
  from disk at runtime.
- **PRISM logo in the agent web UI header**
  (`agent/src/PRISM.Agent/WebUi/IndexHtml.cs` +
  `WebUi/AgentWebUi.cs`): the header now opens with an `<img>` tag
  whose `src` is a `data:image/png;base64,…` URL. `AgentWebUi` reads
  `Assets/prism-logo.png` once on first request, base64-encodes it,
  and caches the rendered HTML for the process lifetime via a
  `Lazy<string>`. The 91 KB PNG becomes ~122 KB inline — still a
  rounding error on the agent's localhost loopback. Falls back to an
  empty `src` if the asset is missing, in which case the page hides
  the broken-image glyph via `img[src=""] { display: none; }`.
- **Inno Setup `IconFilename:` on every shortcut**
  (`agent/install/PRISM.Agent.iss`): Start Menu "PRISM Agent",
  Start Menu "PRISM Agent Web UI", and the optional desktop shortcut
  all explicitly target `{app}\Assets\PRISM.Agent.ico`. Crucial for
  the Web UI shortcut, whose `Filename:` is `http://localhost:7421/`
  — Windows would otherwise render the default browser icon and the
  shortcut would be visually indistinguishable from any other
  bookmark. The Start Menu group as a whole now reads as a coherent
  PRISM-branded entry.
- **Inno Setup `SetupIconFile=...PRISM.Agent.ico`**
  (`agent/install/PRISM.Agent.iss`): the wizard executable
  (`PRISM.Agent-Setup-v0.1.35.exe`) and the wizard window's
  title-bar icon now both show the PRISM logo. Path is relative to
  the `.iss` file, so CI's `ISCC.exe` resolves it against
  `agent/install/`.
- **`tools/make-ico.ps1`** (new): repeatable, ImageMagick-free ICO
  generator used to produce `Assets/PRISM.Agent.ico`. Loads the
  source PNG via `System.Drawing.Image.FromFile`, rasterises each
  requested size with `HighQualityBicubic` interpolation, encodes
  each frame as PNG, and writes a hand-rolled `ICONDIR` + N ×
  `ICONDIRENTRY` + payload container so the .ico stays compact
  (~80 KB instead of >250 KB the all-BMP fallback would produce).
  Re-run when the upstream `PRISM/prism-logo.png` changes.

### Changed

- **Tray icon now shows the PRISM logo at every state**
  (`agent/src/PRISM.Agent/Tray/PrismTrayContext.cs`): the v0.1.34 and
  earlier amber/green/grey coloured-circle state machine has been
  retired. The tray icon loads `Assets/PRISM.Agent.ico` from
  `AppContext.BaseDirectory` and uses it unchanged for the
  Connected, Connecting, and Stopped states. Connection state stays
  discoverable through the existing tooltip ("PRISM Agent —
  Connected / Connecting… / Stopped") and the disabled
  `Status: …` menu item; both already update on every WS reconnect /
  disconnect event. The amber-circle fallback is preserved as
  `LoadLogoIcon()`'s exception/missing-file branch so the tray never
  starts without an icon. Honours the v0.1.34 `SessionId == 0`
  headless guard — `PrismTrayContext` is only constructed in
  interactive sessions, and `LoadLogoIcon()` is invoked from the
  type initialiser as part of that construction.

### Notes

- **Existing v0.1.34 agents need exactly one update cycle to migrate.**
  The new `Assets/` folder, the updated tray icon, and the inline
  web-UI logo all live in the v0.1.35 publish payload — the in-app
  updater (`Updater.DownloadAndInstallAsync`) extracts the zip on
  top of the install dir, so the assets land in
  `C:\Program Files\PRISM.Agent\Assets\` automatically after the
  next successful update. No manual reinstall needed.
- **The new tray icon will not appear on an already-running v0.1.35
  agent until it restarts.** Static-readonly icon fields are bound
  at JIT-init of `PrismTrayContext`; the only way to refresh
  `NotifyIcon.Icon` on a live process is a process restart. The
  built-in `Restart` button on the web UI and the scheduled-task
  auto-relaunch both handle this.
- **No code-signing** still — same posture as v0.1.34 (parked).
- **No DB schema changes, no protocol changes, no server changes.**
  The server image is rebuilt by `server-image` CI because the
  workflow's path filter includes `agent/install/**`, but the
  rebuilt image is byte-equivalent to the v0.1.34 server image in
  every behaviour.

---

## v0.1.34 — 2026-05-27

UX + resilience pass on the in-app updater. Triggered by a v0.1.32
field report from RB-DA2-PC02 ("agent closes but there is no install
window pop up") that the diagnostic subagent traced to the v0.1.32
silent-by-design PowerShell helper — not to Windows Defender. **No
code-signing in this release**: Defender was conclusively ruled out
(zero quarantine/block events for `PRISM.Agent.exe` or the update
zip across the entire log history on PC02), so AD CS signing is
parked for a future cycle.

### Added

- **Agent — visible "Update available" dialog**
  (`agent/.../Tray/UpdateAvailableDialog.cs`): replaces the v0.1.32
  bare-`MessageBox` Yes/No prompt with a proper WinForms dialog that
  shows the new tag, current version, download size (parsed from
  `assets[].size` on the GitHub release JSON), and a scrollable
  preview of the release `body` (release notes). Buttons are
  `Update now` / `Cancel`; `Esc` and the X both cancel safely.
- **Agent — visible "Updating…" progress form**
  (`agent/.../Tray/UpdateProgressForm.cs`): non-modal progress dialog
  shown while `Updater.DownloadAndInstallAsync` runs.  Wired to the
  existing `IProgress<int>` so the bar tracks real download bytes
  (not just a marquee); flips to indeterminate marquee right before
  `Application.Exit()` so the user sees the handoff to the
  PowerShell helper instead of a dead-looking window.
- **Agent — visible PowerShell helper window**
  (`Tray/Updater.cs`): the post-`Application.Exit` extract/relaunch
  PowerShell child now runs with `CreateNoWindow=false` /
  `WindowStyle=Normal` and mirrors every step line to `Write-Host`
  (in addition to the durable `%TEMP%\PRISM.Agent.Update.log` file
  the diagnostic-on-next-startup hook already inspected). The user
  sees `update script started → waiting for agent pid N to exit →
  agent exited → extracting … → extraction complete → launching new
  agent → launched` while it happens. On any `FATAL` line the
  console pauses with `Read-Host 'Press Enter to close'` so the
  operator can copy the diagnostic instead of watching the window
  vanish. On the happy path it auto-closes a couple of seconds
  after `launched`. Pre-v0.1.34 used `CreateNoWindow=true` /
  `-WindowStyle Hidden`, which was the proximate cause of the
  RB-DA2-PC02 user report.
- **Agent — post-update tray balloon**
  (`Tray/PrismTrayContext.cs` + `Tray/Updater.cs`): on startup the
  tray now checks `Updater.ConsumeLastUpdateSuccess()` and, when the
  marker file matches the running assembly version, fires
  `NotifyIcon.ShowBalloonTip(8000, "PRISM Agent updated", "Now
  running v{currentVersion} ({tag}).", ToolTipIcon.Info)` ~2.5 s
  after the icon is realised. The marker is read-and-delete so the
  balloon fires exactly once per actual upgrade.
- **Agent — `Updater.ConsumeLastUpdateSuccess()` + NewVersion
  marker** (`Tray/Updater.cs`): `DownloadAndInstallAsync` now stashes
  the target tag in `%TEMP%\PRISM.Agent.Update.NewVersion` BEFORE
  calling `Application.Exit()`, so the relaunched agent can show the
  post-update balloon without grep-ing the diagnostic log. Stale
  markers (older than 10 min) or markers whose recorded version
  doesn't match the running assembly are deleted silently.
- **Agent — scheduled-task `AtStartup` trigger**
  (`agent/install/install.ps1`): the `PRISM.Agent` task now carries
  two triggers — the existing `AtLogOn -User <currentUser>` plus a
  new `AtStartup`. Combined with the pre-existing `RestartCount=3` /
  `RestartInterval=1m` settings, this means a botched updater that
  exits without successfully relaunching the new agent gets up to
  three additional restart attempts at 1-minute intervals from
  Task Scheduler, AND another shot at boot. Run level remains
  `Highest`; logon type remains `Interactive` (preserving the
  existing principal — no LogonType change required).
- **Agent — session 0 guard** (`Program.cs`): if the agent is ever
  launched in session 0 (no interactive desktop — for example when
  the `AtStartup` trigger is reconfigured to fire pre-logon via
  `S4U` / `Password` logon type), the process forces headless mode
  so the WS + HTTP services still come up cleanly without
  attempting to create a tray icon or message boxes that would
  throw on session 0. Defensive insurance — the shipped principal
  is still `Interactive`, so the guard is a no-op on standard
  installs.

### Changed

- **Agent — `Updater.UpdateInfo` carries SizeBytes + Notes**
  (`Tray/Updater.cs`): `CheckForUpdateAsync` now also parses the zip
  asset's `size` field and the release `body` field so the
  "Update available" dialog can render real numbers and the GitHub
  release notes without making a second API call. Both fields are
  optional and `null` when the release JSON omits them.

### Notes

- **First update from v0.1.32 / v0.1.33 → v0.1.34 still uses the
  OLD silent updater.** The visible-window + tray-balloon + richer
  prompt only applies to updates from v0.1.34 onwards, because the
  updater that runs is whichever one is baked into the currently
  installed agent. Existing workstations will get the "click and
  hope" experience exactly once more (one final silent update) and
  every subsequent update will show the new UI. There is no way
  around this without manually reinstalling v0.1.34 via the wizard
  installer (`PRISM.Agent-Setup-v0.1.34.exe`).
- **No code-signing** in this release. The PC02 diagnostic
  (`agent-transcripts/.../5ecfd18c-...`) showed no Windows Defender
  involvement in the original "no install window" report — the
  fix is squarely a UX / visibility one. AD CS signing remains
  parked for a future release where there's a real Authenticode-
  related symptom to address.
- The new visible PowerShell window is unsigned (everything that
  the agent already runs is unsigned). On workstations where IT
  policy blocks unsigned scripts, the helper is still invoked with
  `-ExecutionPolicy Bypass` from the parent process, matching the
  v0.1.32–v0.1.33 behaviour.

---

## v0.1.33 — 2026-05-27

Adds **remote restart** and **remote update** controls. Admins no longer
have to RDP into a workstation to kick the agent or to make it pull the
latest GitHub release — both actions are reachable from the PRISM admin
Workstations page and from the agent's own web UI.

### Added

- **Agent protocol** (`shared/contracts/agent-protocol.{json,ts}` +
  `shared/contracts/AgentProtocol.cs`): two new server -> agent message
  types — `restart` (optional `reason`) and `update` (optional `tag`
  to pin a release). Older agents (pre-v0.1.33) silently ignore them.

- **Agent — local web UI** (`PRISM/agent/.../WebUi/`): new
  `POST /api/agent/restart` and `POST /api/agent/update` endpoints,
  surfaced as **Check for updates** + **Restart agent** buttons in a
  new "Agent lifecycle" card at the bottom of `http://<host>:7421/`.
  The update endpoint returns either `{ok, downloading: false,
  version}` when already on the latest tag, or `{ok, downloading:
  true, tag}` while it pulls the new zip in the background.

- **Agent — WS handler** (`PRISM/agent/.../Ws/AgentMessageDispatcher.cs`):
  inbound `restart` / `update` envelopes are routed to
  `AgentControlPlane.RestartAsync` / `CheckAndApplyUpdateAsync`. The
  same methods back both the local HTTP endpoints and the admin-driven
  WS commands, so there is exactly one code path per action.

- **Agent — `AgentControlPlane`**: `RestartAsync` schedules a tiny
  hidden PowerShell helper that waits for the agent's PID to exit and
  then relaunches `PRISM.Agent.exe` (same pattern as the in-app
  updater), then exits with code 2 so the Scheduled Task's
  `RestartCount=3` also fires as a belt-and-braces fallback.
  `CheckAndApplyUpdateAsync` reuses
  `Updater.CheckForUpdateAsync` + `DownloadAndInstallAsync` exactly as
  the tray menu does — including the v0.1.32 `IsInstallDirWritable`
  pre-flight and `%TEMP%\PRISM.Agent.Update.log` diagnostic trail.

- **Server — admin API** (`PRISM/server/src/api/workstations.ts`):
  `POST /api/workstations/:id/restart` and
  `POST /api/workstations/:id/update` (admin session required).
  Look the workstation up by id, find the live agent in
  `sessionRegistry` by machineId, dispatch the WS envelope, and
  return `{queued: true}`. 404 if the workstation row is unknown,
  503 if no agent is currently connected. The update endpoint
  optionally accepts `{tag: "v0.1.33"}` to pin a release.

- **Server — outbound dispatchers**
  (`PRISM/server/src/ws/agentProtocol.ts`): new `sendRestartToAgent` /
  `sendUpdateToAgent` helpers wrap the `sessionRegistry` lookup +
  `socket.send(JSON.stringify(envelope(...)))` pattern that the job
  dispatcher uses, so the admin routes don't reach into WS plumbing
  directly.

- **Web — typed client** (`web/src/shared/api.ts`):
  `workstationsApi.restart(id, reason?)` and
  `workstationsApi.updateAgent(id, tag?)` helpers. The admin
  Workstations page wires per-row buttons in a follow-up commit.

### Notes

- Existing **v0.1.32 agents** stay connected after this server deploy
  but won't act on `restart` / `update` envelopes (unknown message
  types are silently ignored). The admin buttons will still return
  `{queued: true}` against them; nothing happens on the workstation
  until that agent is upgraded to v0.1.33 (one-time, via the in-app
  updater or the GitHub release wizard installer). Same pattern as
  the v0.1.32 update-installer rollout documented above.

- The agent's own web UI works against any agent v0.1.33+ regardless
  of server version — the buttons hit the local HTTP listener directly.

---

## v0.1.32 — 2026-05-26

Fix the in-app updater silently failing on workstations whose interactive
user is not a local administrator. Symptom: clicking **Check for updates →
Yes** flashed a CMD/PowerShell window for ~1 s and then nothing happened
(version unchanged, tray came back at the old version after the scheduled
task auto-restarted).

### Fixed

- **Auto-update silently fails on Program Files (ACL)**: the elevated
  PowerShell child process spawned by `Updater.cs` couldn't write to
  `C:\Program Files\PRISM.Agent` because the interactive user wasn't a
  local administrator (the scheduled task's `RunLevel=Highest` only
  promotes admin users; standard users stay standard). Two-pronged fix:
  - **Pre-grant `BUILTIN\Users:(OI)(CI)M` on `$InstallDir`** in
    `install.ps1` via `icacls /grant *S-1-5-32-545:(OI)(CI)M /T`. After
    install (or re-running the wizard once), the agent's PowerShell
    child can extract the new zip on top of Program Files without
    elevation.
  - **`Updater.IsInstallDirWritable()` pre-flight check** before
    downloading: if the install dir is read-only the updater throws a
    clear `UnauthorizedAccessException` ("Please re-run
    PRISM.Agent-Setup.exe (run as administrator) once...") that the
    tray surfaces via `MessageBox`. No more silent CMD-flash mystery.
- **Brief CMD/console flash before update**: the spawned PowerShell
  used `-WindowStyle Hidden`, which only hides the window *after* it's
  created — there was always a 0.5–1 s flash. Switched to
  `ProcessStartInfo.CreateNoWindow = true`, which the kernel applies
  before any console host appears. Update now runs fully silently.
- **Updater script logs to `%TEMP%\PRISM.Agent.Update.log`**: the
  PowerShell update script now wraps every step in `try/catch` and
  appends timestamped status lines (`update script started`, `waiting
  for agent pid N to exit`, `extracting ...`, `extraction complete`,
  `launched`, plus `FATAL: <message>` on any error). The next agent
  startup checks this log and, if it contains a fatal/error and is
  less than 10 minutes old, surfaces it via the agent's structured
  logger so the failure shows up in the tray Logs window and
  `prism-agent.log`.

### Notes

- For workstations already on v0.1.31 with a non-admin login user, the
  in-app update to v0.1.32 will fail with the new clear error message.
  Re-run **PRISM.Agent-Setup-v0.1.32.exe** (right-click → Run as
  administrator) once to apply the ACL grant. From v0.1.32 onward,
  every future update goes through cleanly without elevation.

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
