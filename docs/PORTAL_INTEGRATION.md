# PRISM Visualiser — Portal Integration Guide

**Audience:** third-party developers integrating the PRISM Visualiser into
their own portal.
**Version:** v0.2.0 (cuts at the Visualiser milestone tag — see
[`RELEASE_STRATEGY.md`](RELEASE_STRATEGY.md)).
**Companion:** machine-readable contract at
[`https://prism.rebus.industries/docs`](https://prism.rebus.industries/docs)
(Redoc-rendered OpenAPI 3.1).

---

## Overview

PRISM Visualiser lets your portal embed a live, interactive 3D view of an
[ORBIT](https://github.com/REBUS-ORBIT) project, rendered on a REBUS
workstation in Unreal Engine 5.7 and streamed over WebRTC to the browser via
[Pixel Streaming 2](https://docs.unrealengine.com/5.7/en-US/pixel-streaming-in-unreal-engine/).

The flow from your portal's perspective is five steps:

1.  Your portal mints a short-lived signed token of its own choosing
    (whatever your auth model is — PRISM does not care).
2.  Your portal calls `POST https://prism.rebus.industries/api/visualiser/streams`
    with `projectId` + `modelId` + (optional) `versionId`. The call blocks
    until the agent has spun the workstation up — **~2-3 s warm, ~60-90 s
    cold** (see [Timing budget](#timing-budget)).
3.  PRISM returns a `prism-visualiser/ready/v1` envelope with a
    `signallingUrl`, a `playerUrl`, a `streamerId`, and a `turn` credential
    bundle.
4.  Your portal embeds Epic's
    [`@epicgames-ps/lib-pixelstreamingfrontend-ue5.5`](https://www.npmjs.com/package/@epicgames-ps/lib-pixelstreamingfrontend-ue5.5)
    (or your own WebRTC client) and points it at the signalling URL. You
    inject the TURN credentials into the
    `RTCPeerConnection.iceServers` list before the SDP exchange.
5.  When the user dismisses the viewer, your portal calls
    `DELETE /api/visualiser/streams/{runId}` to free the workstation.

Everything between steps 3 and 5 is plain WebRTC — there is no special
PRISM layer the browser sees beyond signalling. Camera, lighting, and
input events are all interactive in real time.

---

## Architecture

```
   Your portal browser           PRISM server (VM 211)         REBUS workstation
   ───────────────────           ──────────────────────         ─────────────────
   1. POST /streams ───────────► REST handler                   PRISM.Agent.exe
                                       │ enqueue                       │
                                       ▼                               │
                                 Postgres + WS                         │
                                       │ startVisualisation ──────────►│
                                       │                               │
                                       │      ◄─── visualisationReady ─┤
                                       ▼                               │
   2. ◄─ ready envelope ──── REST handler                              │
       (signallingUrl,                                                 │
        turn creds,                                                    │
        playerUrl)                                                     │
                                                                       │
   3. WSS /ws/visualiser/<runId>/signalling                            │
      ◄──────── WebSocket signalling proxy ────────► SignallingBridge ─┤
                                                                       │
   4. ◄─────────── WebRTC media via coturn relay ──────────► UnrealEditor.exe -game
        (visualiser.rebus.industries:3478)                       (Pixel Streaming 2)

   5. DELETE /streams/<runId> ──► REST handler ──► cancelVisualisation ─►│
                                                                         │
                                                              UE exit, cleanup
```

Two planes to keep in mind:

-   **Control plane** (HTTP + WS): your portal ↔ PRISM server ↔ agent ↔
    orchestrator. Round-trip ~2-3 s once UE is warm; ~30-90 s cold.
-   **Media plane** (WebRTC + TURN): browser ↔ coturn (VM 211, public) ↔
    workstation UE. Latency budget ~80-150 ms across the internet.

---

## Authentication

PRISM authenticates portal requests with a header-based API key:

```http
X-API-Key: prism_<base64url>
```

### Obtaining a key

Keys are minted in the PRISM admin UI at
`https://prism.rebus.industries/admin/#/api-keys`. The admin selects which
scopes the key carries — for the visualiser surface a portal key needs:

-   `visualiser:create_stream` — required for `POST /api/visualiser/streams`
    and `DELETE /api/visualiser/streams/{runId}`.
-   `visualiser:attach_project_files` (optional) — only if you want to upload
    MVR/GDTF attachments from the portal. See
    [Project attachments](#project-attachments--mvrgdtf).

Read-only endpoints (`GET`) are gated by `requireAuth` rather than
`requireScope`, so any valid API key, admin cookie, or ORBIT bearer is
accepted.

The plaintext key is shown **once** at mint time. PRISM stores only the
SHA-256 hash; if you lose it, mint a new one and revoke the old.

### Key rotation

Mint the new key, deploy it to your portal, then revoke the old key from
the admin UI. There is no "grace period" — revocation is immediate.
Portals SHOULD rotate keys at least annually.

### Sample-curl

```bash
export PRISM_KEY=prism_xyz
curl -sS -H "X-API-Key: $PRISM_KEY" \
     https://prism.rebus.industries/api/visualiser/streams
# -> { "runs": [], "limit": 50, "offset": 0 }
```

---

## Starting a stream — `POST /api/visualiser/streams`

The single most important call in the integration. Synchronous: blocks
until the workstation hands back a ready envelope, a terminal failure, or
the start deadline (default 180 s) fires.

### Request body

```jsonc
{
  // required
  "projectId":  "cf900606f5",
  "modelId":    "be45d33eb1",

  // optional - omit to materialise the model's latest version
  "versionId":  "v_2026_05_12_001",

  // optional - 'prod' or 'dev'; defaults to 'prod'
  "orbitTarget": "prod",

  // optional - reserved for future use; the dispatcher currently picks
  // the least-loaded eligible workstation
  "preferredWorkstationId": "8a3e...",

  // optional - reserved for future use; the server accepts this field
  // today but does not yet POST status updates to it
  "callbackUrl": "https://portal.example.com/prism/visualiser-events",

  // optional - pin the UE template tag the agent runs against
  "templateTag": "v1.0.0-ue5.7",

  // optional - hard tear-down deadline (seconds) enforced by the orchestrator
  "ttlSeconds": 3600
}
```

### Response (200 OK)

```jsonc
{
  "schema":        "prism-visualiser/ready/v1",
  "runId":         "5b9c1d4f-9d72-4a8c-8e64-7e22b5f2f01b",
  "status":        "streaming",
  "signallingUrl": "wss://prism.rebus.industries/ws/visualiser/5b9c1d4f-9d72-4a8c-8e64-7e22b5f2f01b/signalling",
  "playerUrl":     "https://prism.rebus.industries/admin/#/visualiser/5b9c1d4f-9d72-4a8c-8e64-7e22b5f2f01b",
  "streamerId":    "orbit_5b9c1d4f",
  "turn": {
    "urls":       ["turn:visualiser.rebus.industries:3478",
                   "turns:visualiser.rebus.industries:5349"],
    "username":   "1748284800:5b9c1d4f",
    "credential": "gHrjK0iA0sM...",
    "ttl":        86400
  }
}
```

The `playerUrl` is PRISM's hosted debug viewer. Third-party portals
ignore it and embed `lib-pixelstreamingfrontend` directly.

### Timing budget

| Scenario                                            | Round-trip       |
| --------------------------------------------------- | ---------------- |
| Warm — UE editor cached, same `(projectId, modelId)` already imported | **~2-3 s**       |
| Cold — first run on a workstation, shader compile + import | **~60-90 s**     |
| Hard deadline (`VISUALISER_START_TIMEOUT_MS`)       | **180 s default** |

The orchestrator caches per-`(projectId, modelId)` workspaces on the
workstation, so the first request against a brand-new model is always
cold but subsequent requests against the same model are warm.

If you call POST with a timeout shorter than 180 s on your HTTP client,
**you will hit your own timeout before PRISM gives up**. Set your client
timeout to at least 200 s for cold starts.

### Idempotency

The Phase G implementation **does not** yet deduplicate concurrent
requests for the same `(projectId, modelId, versionId)` triple — two
in-flight POSTs each create a fresh `visualiser_runs` row and race for
the same workstation slot. Portals SHOULD serialise per-(project,
model, version) at their own layer until the v0.3 idempotency
follow-up lands (which will return the existing run on conflict).

### Error codes

| Status | `code`                       | Retry?   | What happened                                                                                                                       |
| ------ | ---------------------------- | -------- | ----------------------------------------------------------------------------------------------------------------------------------- |
| 400    | -                            | No       | Validation failed (missing `projectId`, malformed UUID, etc.).                                                                      |
| 401    | -                            | No       | Missing/invalid `X-API-Key`.                                                                                                        |
| 403    | -                            | No       | Key lacks the `visualiser:create_stream` scope.                                                                                     |
| 429    | -                            | Yes, backoff | Per-key rate limit. Honour `X-RateLimit-Reset`.                                                                                  |
| 500    | `misconfigured`              | No       | Server is missing required env (ORBIT URL, TURN secret, JWT secret). Alert your ops contact.                                        |
| 502    | `agent_failed`               | Maybe    | Workstation tried but failed (GPU pre-flight, UE crash, import error). The `message` field carries detail. Retry once after 30 s.   |
| 503    | `no_workstation_available`   | Yes, backoff | No visualiser-capable workstation is online. Retry after 60 s.                                                                  |
| 503    | `all_workstations_busy`      | Yes, backoff | Every workstation is at its single-tenant slot cap. Retry after 60 s.                                                            |
| 504    | `start_timeout`              | Maybe    | Agent did not reply within 180 s — usually a wedged Unreal process. Retry once; if it persists, alert your ops contact.             |

Failed responses follow the `prism-visualiser/failed/v1` schema:

```json
{
  "schema":  "prism-visualiser/failed/v1",
  "runId":   "5b9c1d4f-...",
  "error":   "visualisation_failed",
  "code":    "start_timeout",
  "message": "start exceeded 180000ms"
}
```

### Code samples

#### bash + curl

```bash
PRISM_KEY=prism_xyz
curl -sS -X POST https://prism.rebus.industries/api/visualiser/streams \
     -H "X-API-Key: $PRISM_KEY" \
     -H "Content-Type: application/json" \
     --max-time 200 \
     -d '{
       "projectId": "cf900606f5",
       "modelId":   "be45d33eb1"
     }'
```

#### Node + fetch (TypeScript)

```typescript
const PRISM_BASE = 'https://prism.rebus.industries';

interface ReadyEnvelope {
  schema: 'prism-visualiser/ready/v1';
  runId: string;
  status: 'streaming';
  signallingUrl: string;
  playerUrl: string;
  streamerId: string;
  turn: {
    urls: string[];
    username: string;
    credential: string;
    ttl: number;
  } | null;
}

async function startStream(
  projectId: string,
  modelId: string,
  apiKey: string,
): Promise<ReadyEnvelope> {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), 200_000);  // 200s > 180s server timeout
  try {
    const res = await fetch(`${PRISM_BASE}/api/visualiser/streams`, {
      method: 'POST',
      headers: {
        'X-API-Key': apiKey,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ projectId, modelId }),
      signal: ctrl.signal,
    });
    if (!res.ok) {
      const body = await res.json().catch(() => ({ error: res.statusText }));
      throw new Error(`prism start failed: ${res.status} ${JSON.stringify(body)}`);
    }
    return (await res.json()) as ReadyEnvelope;
  } finally {
    clearTimeout(t);
  }
}
```

#### Python + requests

```python
import requests

PRISM_BASE = 'https://prism.rebus.industries'

def start_stream(project_id: str, model_id: str, api_key: str) -> dict:
    res = requests.post(
        f'{PRISM_BASE}/api/visualiser/streams',
        headers={'X-API-Key': api_key, 'Content-Type': 'application/json'},
        json={'projectId': project_id, 'modelId': model_id},
        timeout=200,  # 200s > 180s server timeout
    )
    if not res.ok:
        raise RuntimeError(f'prism start failed: {res.status_code} {res.text}')
    return res.json()
```

---

## Polling status — `GET /api/visualiser/streams/{runId}`

In normal operation you do not need to poll — the synchronous POST
already blocks until the run is ready. Polling is useful for:

-   Resuming a session after a browser refresh (your portal stored the
    `runId`, now it wants to know if the workstation is still streaming).
-   Surfacing live status in a "stream is running" admin panel.
-   Recovering from a lost POST response (network blip mid-flight).

### When to poll

When `status` is `streaming`, **every 30 s** is plenty. While the
workstation is still importing, **every 5 s** is fine. Don't poll terminal
states (`failed`, `ended`).

### Response

The single-row GET returns the full `VisualiserRun` schema (see the
[OpenAPI spec](https://prism.rebus.industries/docs#tag/Visualiser) for
the field-by-field reference). While `status === 'streaming'`, the
response includes a **fresh** `turn` bundle so your browser can use it
to reconnect if the existing PeerConnection drops.

```bash
curl -sS -H "X-API-Key: $PRISM_KEY" \
     https://prism.rebus.industries/api/visualiser/streams/5b9c1d4f-9d72-4a8c-8e64-7e22b5f2f01b
```

### Status transitions

```
                              ┌── failed ───┐
                              │             │
                              ▼             │
queued ─► importing ─► streaming ─► ended ──┴── terminal
                              │
                              └── failed (terminal)
```

`queued` is brief (< 100 ms). `importing` lasts the cold-start window.
`streaming` lasts until your portal sends DELETE, the TTL expires, or
the browser disconnects.

---

## Embedding the player

PRISM does not ship a portal-facing player widget — you embed Epic's
official Pixel Streaming frontend library directly. Two options:

1.  **Recommended**:
    [`@epicgames-ps/lib-pixelstreamingfrontend-ue5.5`](https://www.npmjs.com/package/@epicgames-ps/lib-pixelstreamingfrontend-ue5.5)
    (~50 KB minified, the core renderer, API-stable for UE 5.5 → 5.7
    streamers per Epic's release notes). PRISM's own admin debug viewer
    uses this exact package and pins to `1.2.5`.
2.  Hand-rolled WebRTC client. Fine for advanced use cases (you control
    the iceServers list, codec selection, etc.) but not recommended for
    a first integration.

### Mint a signalling token

The signalling WS is JWT-gated. Before opening the WS, mint a token:

```http
POST /api/visualiser/streams/{runId}/signalling-token
X-API-Key: prism_xyz
```

```jsonc
{
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "exp":   1748284800
}
```

Append it as `?token=…` to the `signallingUrl`. Tokens are bound to the
runId, not the socket — a single token works for the whole session as
long as you don't reconnect after 5 min.

### React example

```tsx
import { PixelStreaming, Config } from '@epicgames-ps/lib-pixelstreamingfrontend-ue5.5';
import { useEffect, useRef } from 'react';

interface Props {
  signallingUrl: string;           // from the ready envelope
  runId: string;                   // from the ready envelope
  apiKey: string;                  // your PRISM key
  turn: {                          // from the ready envelope
    urls: string[];
    username: string;
    credential: string;
  } | null;
}

export function PrismVisualiserPlayer({ signallingUrl, runId, apiKey, turn }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!containerRef.current) return;
    let cancelled = false;
    let ps: PixelStreaming | undefined;

    (async () => {
      // 1. Mint a fresh signalling token.
      const tokRes = await fetch(
        `https://prism.rebus.industries/api/visualiser/streams/${runId}/signalling-token`,
        { method: 'POST', headers: { 'X-API-Key': apiKey } },
      );
      if (!tokRes.ok) throw new Error('token mint failed');
      const { token } = await tokRes.json();
      if (cancelled) return;

      // 2. Build the signalling URL with the token.
      const url = new URL(signallingUrl);
      url.searchParams.set('token', token);

      // 3. Inject TURN creds into the lib's WebRTC config before connection.
      const config = new Config({
        useUrlParams: false,
        initialSettings: { ss: url.toString(), AutoConnect: true },
      });
      ps = new PixelStreaming(config, { videoElementParent: containerRef.current! });

      // Monkey-patch the iceServers list. The PS frontend lib reads ICE
      // servers from Cirrus's `config` message; we override it so coturn
      // is always present in the candidate set.
      const ctrl: any = (ps as any).webRtcController;
      if (turn && ctrl?.handleOnConfigMessage) {
        const orig = ctrl.handleOnConfigMessage.bind(ctrl);
        ctrl.handleOnConfigMessage = function (msg: any) {
          msg.peerConnectionOptions ??= {};
          msg.peerConnectionOptions.iceServers ??= [];
          msg.peerConnectionOptions.iceServers.push({
            urls: turn.urls,
            username: turn.username,
            credential: turn.credential,
          });
          return orig(msg);
        };
      }
    })().catch(console.error);

    return () => {
      cancelled = true;
      ps?.disconnect();
    };
  }, [signallingUrl, runId, apiKey, turn]);

  return <div ref={containerRef} style={{ width: '100%', height: '100%' }} />;
}
```

### Vue 3 example

```vue
<script setup lang="ts">
import { PixelStreaming, Config } from '@epicgames-ps/lib-pixelstreamingfrontend-ue5.5';
import { onMounted, onBeforeUnmount, ref } from 'vue';

const props = defineProps<{
  signallingUrl: string;
  runId: string;
  apiKey: string;
  turn: { urls: string[]; username: string; credential: string } | null;
}>();

const container = ref<HTMLDivElement | null>(null);
let ps: PixelStreaming | undefined;

onMounted(async () => {
  if (!container.value) return;
  const tokRes = await fetch(
    `https://prism.rebus.industries/api/visualiser/streams/${props.runId}/signalling-token`,
    { method: 'POST', headers: { 'X-API-Key': props.apiKey } },
  );
  const { token } = await tokRes.json();
  const url = new URL(props.signallingUrl);
  url.searchParams.set('token', token);
  const config = new Config({
    useUrlParams: false,
    initialSettings: { ss: url.toString(), AutoConnect: true },
  });
  ps = new PixelStreaming(config, { videoElementParent: container.value });
  // ... iceServers monkey-patch identical to the React example
});

onBeforeUnmount(() => {
  ps?.disconnect();
});
</script>

<template>
  <div ref="container" class="prism-player" />
</template>

<style scoped>
.prism-player { width: 100%; height: 100%; }
</style>
```

### Vanilla JS (no framework)

```html
<!doctype html>
<html>
  <body>
    <div id="player" style="width:1280px;height:720px"></div>
    <script type="module">
      import { PixelStreaming, Config } from 'https://cdn.jsdelivr.net/npm/@epicgames-ps/lib-pixelstreamingfrontend-ue5.5@1.2.5/+esm';

      const ready = window.__prismReady;  // your portal stashed this from POST /streams
      const tokRes = await fetch(
        `https://prism.rebus.industries/api/visualiser/streams/${ready.runId}/signalling-token`,
        { method: 'POST', headers: { 'X-API-Key': window.PRISM_API_KEY } },
      );
      const { token } = await tokRes.json();
      const url = new URL(ready.signallingUrl);
      url.searchParams.set('token', token);

      const config = new Config({
        useUrlParams: false,
        initialSettings: { ss: url.toString(), AutoConnect: true },
      });
      const ps = new PixelStreaming(config, {
        videoElementParent: document.getElementById('player'),
      });

      // iceServers monkey-patch (see the README example) ...
    </script>
  </body>
</html>
```

### Token refresh for long-lived sessions

The signalling JWT expires after 5 min. The PeerConnection itself stays
alive for the full session because WebRTC ICE keep-alive runs on the
media plane, not the signalling plane. **You only need to refresh the
token if the signalling socket reconnects.** The PS frontend lib opens
the signalling WS once on `connect()` and keeps it open; if it drops
(network blip), mint a new token and call `connect()` again.

---

## TURN credentials

PRISM's coturn relay lives at `visualiser.rebus.industries` (VM 211,
public IP). The portal does **not** see coturn directly — PRISM mints
RFC 7635-compatible long-term credentials from a server-side shared
secret and returns them in the `turn` field of the ready envelope.

```json
"turn": {
  "urls": [
    "turn:visualiser.rebus.industries:3478",
    "turns:visualiser.rebus.industries:5349"
  ],
  "username":   "1748284800:5b9c1d4f",
  "credential": "gHrjK0iA0sM...",
  "ttl": 86400
}
```

### Properties

-   **HMAC-derived**, not random. coturn validates the credential against
    the shared `TURN_SECRET` without keeping any per-credential state.
-   **24h TTL** by default. The `username` field encodes the Unix
    expiry as `<expiry>:<runId>`.
-   **Per-stream**. Every ready envelope (and every `GET /streams/{runId}`
    response while streaming) gives you a fresh bundle — they're cheap
    to generate.

### Refresh policy

You almost certainly do not need to refresh during a session. The 24h TTL
covers the longest plausible streaming window, and the credential is
valid for the full lifetime of the PeerConnection that consumed it.
Refresh only if:

-   The session is >23h old (rare).
-   The PeerConnection failed and is being rebuilt (call
    `GET /streams/{runId}` to get a new bundle, then reconnect).

### Why `turn:` AND `turns:`?

`turn:` is plain DTLS over UDP — lower latency, but blocked by some
corporate firewalls. `turns:` is TLS-over-TCP, slower but reaches every
network. The PS frontend lib tries `turn:` first and falls back to `turns:`
when ICE fails on the cheaper path. **Always pass both.**

### `turn: null` — what to do

If `turn` is `null` in the ready envelope, the PRISM server has not been
configured with `TURN_SECRET` yet (dev environments without Phase H
deployed). You can still try the stream — it will work on the LAN —
but cross-internet WebRTC will fail. Alert your ops contact.

---

## Errors and retry policy

| Symptom                              | Action                                                                                                  |
| ------------------------------------ | ------------------------------------------------------------------------------------------------------- |
| POST returns 429                     | Honour `X-RateLimit-Reset`. Exponential backoff, max 5 min.                                             |
| POST returns 503 (`no_workstation_available`) | Wait 60 s, retry. After 3 failures, surface "no workstation available" to the user.                 |
| POST returns 504 (`start_timeout`)   | Retry **once** after 30 s. If it fails again, the workstation is likely wedged — alert your ops contact.|
| POST returns 502 (`agent_failed`)    | Inspect `message`. GPU pre-flight failures (`gpu_preflight_failed`) are recoverable on a different workstation; retry with `preferredWorkstationId` unset. |
| POST returns 500 (`misconfigured`)   | Do not retry. Alert your ops contact.                                                                   |
| Stream goes dark mid-session         | Call `GET /streams/{runId}`. If `status` is still `streaming`, reconnect the PeerConnection with a fresh token + TURN bundle. If `status` is `failed`/`ended`, the workstation is gone — start a new stream. |
| Workstation goes offline mid-stream  | The run transitions to `failed`. Your portal should `DELETE` the run (no-op if already terminal) and start a fresh one. |
| Pixel Streaming player shows a black frame for >5 s | Usually a TURN/ICE failure. Check the browser console for ICE-failed errors; the PS lib surfaces them via `onWebRtcDisconnected`. Fall back to a fresh `runId`. |

### Timeout handling

-   Your HTTP client timeout for POST `/streams` MUST be > 180 s (the
    server-side `VISUALISER_START_TIMEOUT_MS`). 200 s is a safe value.
-   GET `/streams/{runId}` is fast (< 100 ms); 5 s timeout is fine.
-   DELETE `/streams/{runId}` is best-effort; 10 s timeout is fine — if
    it times out, the run will TTL out on its own.

---

## Project attachments — MVR / GDTF

**Optional.** Use this only if your portal lets users associate MVR
([My Virtual Rig](https://gdtf-share.com/wiki/MVR)) or GDTF
([General Device Type Format](https://gdtf-share.com/)) lighting data
with an ORBIT project. The orchestrator runs a second-pass
`import_mvr.py` after `import_orbit.py` whenever at least one MVR or
GDTF attachment is present.

This is a Phase J feature; v1 source-side support is "upload via this
endpoint" — no Rhino/Vectorworks connector emits MVR data yet (tracked
as a Visualiser v0.3 follow-up).

### Upload — `POST /api/projects/{projectId}/attachments`

```bash
curl -sS -X POST \
     -H "X-API-Key: $PRISM_KEY" \
     -F "file=@/path/to/show_2026.mvr" \
     https://prism.rebus.industries/api/projects/cf900606f5/attachments
# -> 201 Created
# {
#   "id":          "8a3e...",
#   "projectId":   "cf900606f5",
#   "filename":    "show_2026.mvr",
#   "contentType": "application/mvr",
#   "sizeBytes":   348572,
#   "uploadedAt":  "2026-05-27T17:30:00Z",
#   "uploadedByApiKeyId": "..."
# }
```

-   Requires the `visualiser:attach_project_files` scope.
-   Multipart upload, single `file` part.
-   50 MB hard cap.
-   Allowed extensions: `.mvr`, `.gdtf`, `.zip` (bundles of MVR/GDTF).
-   Soft-delete via `DELETE /api/projects/{projectId}/attachments/{id}`.

Attachments are pulled into the run's stage directory by the
orchestrator at the start of every visualiser run; uploads MUST land
before the POST `/streams` call.

---

## Out of scope (v1)

The following are explicit non-goals for the v0.2.0 Visualiser milestone.
Plan around them; do not file bugs.

-   **Sending data back to ORBIT.** The stream is one-way. The user can
    rotate the camera and toggle lighting locally; nothing they do
    persists upstream.
-   **Multi-tenant concurrent streams per workstation.** Single-tenant in
    v1. If two POSTs hit the same workstation, the second waits its turn
    or is dispatched to a different workstation.
-   **LAN-only mode.** The relay always goes through coturn — even for
    same-LAN browser ↔ workstation pairs. This simplifies the contract
    (one TURN bundle works everywhere). A future v2 might add a LAN
    short-circuit.
-   **Live updates as source CAD changes.** Each run materialises a
    snapshot of the ORBIT version at request time. To reflect new CAD
    edits, start a new stream against the new version.
-   **Connector-side MVR/GDTF extraction.** No Rhino or Vectorworks
    connector emits MVR data today. Upload via the project-attachments
    endpoint until v0.3 ships the connector work.
-   **Persistent player URL.** The `playerUrl` field is a PRISM debug
    viewer convenience; do not expose it to end users. Embed the lib
    in your own UI.
-   **Webhook callbacks on ready/failed.** The `callbackUrl` field on
    `POST /streams` is accepted but not yet dispatched. Poll
    `GET /streams/{runId}` instead. (Tracked for v0.3.)

---

## See also

-   Machine-readable contract: [`https://prism.rebus.industries/docs`](https://prism.rebus.industries/docs)
-   Release strategy: [`RELEASE_STRATEGY.md`](RELEASE_STRATEGY.md)
-   AV exclusions on workstations: [`ANTIVIRUS_EXCLUSIONS.md`](ANTIVIRUS_EXCLUSIONS.md)
-   Scheduled-task resilience: [`SCHEDULED_TASK_RESILIENCE.md`](SCHEDULED_TASK_RESILIENCE.md)
-   PR merge order for the v0.2.0 release: [`VISUALISER_MERGE_ORDER.md`](VISUALISER_MERGE_ORDER.md)
-   Source: [`github.com/REBUS-ORBIT/prism`](https://github.com/REBUS-ORBIT/prism)
