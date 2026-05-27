# PRISM server — infrastructure setup notes

Companion to `DEPLOY.md`. Where `DEPLOY.md` is the *runbook* for putting
PRISM server on VM 211, this file documents the **adjacent infra
dependencies** that PRISM relies on but does not own: coturn for
WebRTC, the Caddy proxy pair for ingress, the UniFi gateway for NAT.

These are kept here (rather than in `DEPLOY.md`) because the matching
**deployment configs live outside the PRISM repo**, in the workspace
infra folder under `D:\Documents\Claude\REBUS System\`. The PRISM repo
documents the contract; the workspace folder holds the actual
artifacts. This file is the index between them.

---

## Visualiser stack (Phase H +)

The Visualiser feature needs three pieces of infrastructure that PRISM
server itself does not provision:

1. **coturn TURN/STUN server.** Required for the browser-to-workstation
   WebRTC media relay. Deployed on VM 211 alongside PRISM server.
   - Deployment files:
     `D:\Documents\Claude\REBUS System\TURN\docker-compose.yml`
     `D:\Documents\Claude\REBUS System\TURN\turnserver.conf`
   - Runbook: `D:\Documents\Claude\REBUS System\TURN\SETUP_NOTES.md`
   - Public DNS: `visualiser.rebus.industries` → `185.48.165.165`
     (NAT'd to `10.0.200.211`).

2. **UniFi gateway port-forwards.** Open the public ports coturn needs.
   - Rule table: `D:\Documents\Claude\REBUS System\TURN\UNIFI_RULES.md`
   - Applied via UniFi Console → Settings → Internet → Port Forwarding.
   - Without these rules, `POST /api/visualiser/streams` returns valid
     credentials but browsers cannot reach the relay (ICE candidate
     gathering completes but no `relay` candidates appear).

3. **Caddy ACME-only block for `visualiser.rebus.industries`.** Caddy
   does NOT proxy TURN — it only serves the HTTP-01 challenge so the
   hostname has a cert. Block lives in:
   `D:\Documents\Claude\REBUS System\proxy\Caddyfile`
   coturn obtains its own cert via certbot on VM 211; the Caddy-issued
   cert is currently unused and is held as a fallback / future option.

### TURN_SECRET wiring

PRISM server consumes `TURN_SECRET` via
`server/src/visualiser/turnCredentials.ts`. The chain is:

```
infra/.env.example                docs the variable (commit-safe template)
   │
   ▼
/opt/prism/.env on VM 211         operator-edited at deploy time
   │
   ▼
infra/docker-compose.yml          passes ${TURN_SECRET:-} into container
   │
   ▼
prism-server container env        process.env.TURN_SECRET
   │
   ▼
turnCredentials.ts                base64(HMAC-SHA1(TURN_SECRET, "<exp>:<tag>"))
   │
   ▼
POST /api/visualiser/streams      .turn.{urls, username, credential, ttl}
```

The **same** value MUST be set in coturn's `turnserver.conf` as
`static-auth-secret=`. Mismatch shows up as coturn `441 Wrong
Credentials` log lines and clients failing to authenticate against the
relay.

Two related envs land in the same Phase H wiring:

- `JWT_SIGNALLING_SECRET` (HS256 signing key for the 5-minute
  WS-signalling tokens minted by
  `POST /streams/:runId/signalling-token`). Must be a strong random
  value; rotate independently of `TURN_SECRET`.
- `VISUALISER_START_TIMEOUT_MS` (default 180000). Tune if you observe
  cold-start times exceeding 180s in practice.

`infra/.env.example` documents all three with the canonical
`openssl rand -hex 32` generation pattern.

### Deploy checklist

When standing up the Visualiser stack for the first time, follow this
order:

1. Read `D:\Documents\Claude\REBUS System\TURN\SETUP_NOTES.md` end to
   end before touching anything — it's the source of truth.
2. Generate `TURN_SECRET` and `JWT_SIGNALLING_SECRET` (two separate
   `openssl rand -hex 32` runs).
3. Edit `/opt/prism/.env` on VM 211 to set both, plus
   `TURN_REALM=visualiser.rebus.industries` and
   `VISUALISER_START_TIMEOUT_MS=180000`.
4. Stage and start coturn per the TURN folder's runbook (steps 2-5).
5. `cd /opt/prism && docker compose restart prism-server` to pick up
   the new env.
6. Apply the UniFi rules from `UNIFI_RULES.md`.
7. Add the public + internal DNS records (TURN/SETUP_NOTES.md step 8).
8. Issue the TLS cert via certbot for `turns://` on 5349 (step 9).
9. Smoke-test from a browser on cellular using the Trickle ICE page
   (step 10).

---

## Caddy proxy (Phase 1+)

PRISM server is fronted by the existing Caddy LXC pair (LXC 251 + 252
on `10.0.200.250`). See `infra/Caddyfile.snippet` for the block that
must be present on both proxies; full proxy operations live in
`D:\Documents\Claude\REBUS System\proxy\SETUP_NOTES.md`.

WebSocket support is required (`/ws/agent` for agents,
`/ws/visualiser/:runId/signalling` for Phase H+) — the snippet's `@ws`
matcher takes care of that.

---

## File locations

| Concern | Path |
|---|---|
| PRISM server compose | `PRISM/infra/docker-compose.yml` |
| PRISM env template | `PRISM/infra/.env.example` |
| PRISM Caddy snippet | `PRISM/infra/Caddyfile.snippet` |
| PRISM deploy runbook | `PRISM/DEPLOY.md` |
| coturn compose + conf | `D:\Documents\Claude\REBUS System\TURN\` |
| coturn runbook | `D:\Documents\Claude\REBUS System\TURN\SETUP_NOTES.md` |
| UniFi port-forward rules | `D:\Documents\Claude\REBUS System\TURN\UNIFI_RULES.md` |
| Caddy proxy ops | `D:\Documents\Claude\REBUS System\proxy\` |
