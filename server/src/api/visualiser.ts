/**
 * /api/visualiser/* — start, poll, stop, list Pixel Streaming runs.
 *
 * Surface (see [.cursor/plans/prism_visualiser_role.plan.md], "Portal → PRISM API"):
 *
 *   POST   /api/visualiser/streams
 *     Auth: requireApiKey + requireScope('visualiser:create_stream')
 *     Synchronous: blocks until the agent reports the run ready
 *     (warm ~2-3 s, cold ~60-90 s, timeout default 180 s). Returns the
 *     `prism-visualiser/ready/v1` envelope.
 *
 *   GET    /api/visualiser/streams
 *     Auth: requireAuth
 *     List recent runs (newest first); admin SPA polls this for the
 *     Visualiser page.
 *
 *   GET    /api/visualiser/streams/:runId
 *     Auth: requireAuth
 *     Single-row status poll; surfaces the latest persisted state.
 *
 *   DELETE /api/visualiser/streams/:runId
 *     Auth: requireApiKey (matching `requested_by_api_key_id`) OR admin
 *     Sends `cancelVisualisation` to the agent and marks the row `ended`.
 *
 *   POST   /api/visualiser/streams/:runId/signalling-token
 *     Auth: requireApiKey OR admin (must own the run)
 *     Mints a short-lived HS256 JWT the browser passes to the
 *     signalling WS at `?token=…`. See ws/signallingProxy.ts.
 *
 *   GET    /api/visualiser/workstations
 *     Auth: requireAdmin
 *     Lists eligible workstations (`can_visualise = true` + online),
 *     feeding the admin UI "Start new stream" dropdown.
 *
 * Lifecycle (POST happy path):
 *   1. Validate body.
 *   2. Insert `visualiser_runs` row with `status: 'queued'`,
 *      `requestedByApiKeyId` if applicable.
 *   3. `tryDispatchVisualisation()` reserves a workstation atomically
 *      and sends the `startVisualisation` envelope.
 *   4. Register a Promise waiter (see `runRegistry.ts`); the inbound
 *      `visualisationReady` / `visualisationFailed` WS handler resolves
 *      or rejects it (see ws/agentProtocol.ts).
 *   5. On resolve: build the `prism-visualiser/ready/v1` response,
 *      persist `streamerId` / `signallingUrl` / `playerUrl`, return 200.
 *   6. On reject: persist failureReason + return 502 / 504.
 */
import { randomUUID } from 'node:crypto';
import type { FastifyPluginAsync, FastifyRequest } from 'fastify';
import { z } from 'zod';
import { desc, eq, inArray } from 'drizzle-orm';
import { db } from '../db/client.js';
import { agentSessions, visualiserRuns, workstations, type VisualiserRun } from '../db/schema.js';
import { requireAdmin, requireAuth, requireScope } from '../auth/middleware.js';
import { envelope, type CancelVisualisationData } from '../../../shared/contracts/agent-protocol.js';
import { sessionRegistry } from '../ws/sessionRegistry.js';
import { broadcastWorkstationUpdate } from '../ws/adminProtocol.js';
import { releaseVisualiserSlot, tryDispatchVisualisation } from '../jobs/dispatcher.js';
import { visualiserRunRegistry } from '../visualiser/runRegistry.js';
import { generateTurnCredential } from '../visualiser/turnCredentials.js';
import { issueSignallingToken } from '../visualiser/signallingToken.js';

const START_TIMEOUT_MS = Number(process.env.VISUALISER_START_TIMEOUT_MS ?? 180_000);

const PUBLIC_BASE_URL =
  process.env.PUBLIC_BASE_URL
  ?? process.env.PRISM_PUBLIC_URL
  ?? 'https://prism.rebus.industries';

const READY_SCHEMA_VERSION = 'prism-visualiser/ready/v1';
const FAILED_SCHEMA_VERSION = 'prism-visualiser/failed/v1';

const startBody = z.object({
  projectId:   z.string().min(1),
  modelId:     z.string().min(1),
  versionId:   z.string().min(1).optional(),
  /** Optional ORBIT target — defaults to `prod` to match the jobs surface. */
  orbitTarget: z.enum(['prod', 'dev']).default('prod'),
  /** Reserved for future use; the dispatcher currently picks the least-loaded. */
  preferredWorkstationId: z.string().uuid().optional(),
  /** Reserved for future use; the portal contract documents this for status callbacks. */
  callbackUrl: z.string().url().optional(),
  templateTag: z.string().optional(),
  ttlSeconds:  z.number().int().positive().optional(),
});

const listQuery = z.object({
  status: z.string().optional(),  // comma-separated list of statuses
  limit:  z.coerce.number().int().min(1).max(500).default(50),
  offset: z.coerce.number().int().min(0).default(0),
});

/* -------------------------------------------------------------------------- */
/* Helpers                                                                    */
/* -------------------------------------------------------------------------- */

function buildPlayerUrl(runId: string): string {
  // Admin SPA uses hash-history routing (see web/src/admin/main.ts), so the
  // deep-link is `…/admin/#/visualiser/<runId>`. Phase I will swap this for a
  // dedicated `/visualiser/<runId>/player` static page; until then the admin
  // UI's VisualiserViewer.vue handles the embed.
  return `${PUBLIC_BASE_URL.replace(/\/+$/, '')}/admin/#/visualiser/${runId}`;
}

function buildSignallingUrl(runId: string): string {
  const base = PUBLIC_BASE_URL.replace(/\/+$/, '');
  // Swap http://… → ws://…, https://… → wss://…. Leave non-http schemes
  // alone so the override env var can point at a development relay.
  const wsBase = base.replace(/^http:\/\//, 'ws://').replace(/^https:\/\//, 'wss://');
  return `${wsBase}/ws/visualiser/${runId}/signalling`;
}

function toPublicRun(row: VisualiserRun, opts?: { withTurn?: boolean }) {
  // Phase I: when a caller is about to open the live player (i.e. the
  // single-row GET on `/streams/:runId`), mint a fresh TURN bundle and
  // attach it to the response. We deliberately do NOT mint credentials
  // for the list endpoint — that path is admin polling and the bundle
  // would be unused (and would leak into shared SSE caches if we ever
  // broadcast it). The TURN secret has a 24h TTL by default, so the
  // admin clicking "Refresh" naturally renews it.
  const turn = opts?.withTurn && row.status === 'streaming'
    ? generateTurnCredential({ runId: row.id })
    : undefined;
  return {
    id: row.id,
    status: row.status,
    orbitTarget: row.orbitTarget,
    projectId: row.projectId,
    modelId: row.modelId,
    versionId: row.versionId,
    templateTag: row.templateTag,
    workstationId: row.workstationId,
    agentSessionId: row.agentSessionId,
    signallingUrl: row.signallingUrl,
    playerUrl: row.playerUrl,
    streamerId: row.streamerId,
    failureReason: row.failureReason,
    error: row.error,
    ttlSeconds: row.ttlSeconds,
    submittedBy: row.submittedBy,
    requestedByApiKeyId: row.requestedByApiKeyId,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
    dispatchedAt: row.dispatchedAt,
    readyAt: row.readyAt,
    endedAt: row.endedAt,
    ...(turn !== undefined ? { turn } : {}),
  };
}

function principalSubject(req: FastifyRequest): { submittedBy: string; requestedByApiKeyId: string | null } {
  const p = req.principal;
  if (!p) return { submittedBy: 'anonymous', requestedByApiKeyId: null };
  switch (p.kind) {
    case 'apiKey':       return { submittedBy: `apiKey:${p.apiKeyId}`, requestedByApiKeyId: p.apiKeyId };
    case 'adminSession': return { submittedBy: `admin:${p.username}`, requestedByApiKeyId: null };
    case 'orbitUser':    return { submittedBy: `orbit:${p.userId}`, requestedByApiKeyId: null };
  }
}

async function ownerCanCancel(run: VisualiserRun, req: FastifyRequest): Promise<boolean> {
  const p = req.principal;
  if (!p) return false;
  if (p.kind === 'adminSession') return true;
  if (p.kind === 'apiKey') {
    // The strict-FK path; pre-Phase-G runs may not have the column set
    // (they predate the migration), so fall back to `submittedBy`
    // string match for backwards compat.
    if (run.requestedByApiKeyId) return run.requestedByApiKeyId === p.apiKeyId;
    return run.submittedBy === `apiKey:${p.apiKeyId}`;
  }
  return false;
}

/* -------------------------------------------------------------------------- */
/* Plugin                                                                     */
/* -------------------------------------------------------------------------- */

const plugin: FastifyPluginAsync = async (app) => {
  /* ---------- POST /api/visualiser/streams ---------- */
  // Portal-facing route. Requires the visualiser:create_stream scope —
  // admin sessions and ORBIT bearers bypass scope checks (see
  // requireScope() docs), so the admin SPA "Start new stream" button
  // hits this same endpoint via cookie auth.
  app.post('/streams', {
    preHandler: [requireAuth, requireScope('visualiser:create_stream')],
  }, async (req, reply) => {
    const parsed = startBody.safeParse(req.body);
    if (!parsed.success) {
      return reply.code(400).send({ error: 'invalid body', issues: parsed.error.issues });
    }

    const { submittedBy, requestedByApiKeyId } = principalSubject(req);

    const inserted = await db
      .insert(visualiserRuns)
      .values({
        status: 'queued',
        orbitTarget: parsed.data.orbitTarget,
        projectId: parsed.data.projectId,
        modelId: parsed.data.modelId,
        versionId: parsed.data.versionId ?? null,
        templateTag: parsed.data.templateTag ?? null,
        ttlSeconds: parsed.data.ttlSeconds ?? null,
        callbackUrl: parsed.data.callbackUrl ?? null,
        submittedBy,
        requestedByApiKeyId,
      })
      .returning();
    const run = inserted[0]!;
    const runId = run.id;

    // Register the waiter BEFORE dispatching so an extremely fast
    // agent (or a test double) can't resolve the runId before we have
    // a listener.
    const waiter = visualiserRunRegistry.waitFor(runId, START_TIMEOUT_MS);

    const dispatch = await tryDispatchVisualisation(runId, req.log);
    if (!dispatch.dispatched) {
      visualiserRunRegistry.abandon(runId);
      const failureReason = dispatch.error;
      await db
        .update(visualiserRuns)
        .set({ status: 'failed', failureReason, error: dispatch.reason, updatedAt: new Date(), endedAt: new Date() })
        .where(eq(visualiserRuns.id, runId));
      const status =
        dispatch.error === 'no_workstation_available' || dispatch.error === 'all_workstations_busy' ? 503
        : dispatch.error === 'misconfigured' ? 500
        : 502;
      return reply.code(status).send({
        schema: FAILED_SCHEMA_VERSION,
        runId,
        error: 'dispatch_failed',
        code: dispatch.error,
        message: dispatch.reason,
      });
    }

    // Block until the agent reports ready / failed or the timeout fires.
    let readyEvent;
    try {
      readyEvent = await waiter;
    } catch (failure) {
      const f = failure as { code: string; message: string; stack?: string };
      const isTimeout = f.code === 'start_timeout';
      await db
        .update(visualiserRuns)
        .set({
          status: 'failed',
          failureReason: f.code,
          error: f.message,
          endedAt: new Date(),
          updatedAt: new Date(),
        })
        .where(eq(visualiserRuns.id, runId));

      // Roll back the workstation slot reservation either way — the
      // agent may have crashed mid-import, or simply not responded
      // within the deadline.
      if (dispatch.workstationId) await releaseVisualiserSlot(dispatch.workstationId).catch(() => null);

      if (isTimeout) {
        // Best-effort cancel — the agent may eventually wake up and
        // hand us a `visualisationReady` that we then ignore. The
        // registry's `abandon` keeps state tidy.
        try {
          const conn = sessionRegistry.getAgent(dispatch.agentSessionId);
          if (conn) {
            const cancel: CancelVisualisationData = { runId, reason: 'start_timeout' };
            conn.socket.send(JSON.stringify(envelope('cancelVisualisation', cancel, randomUUID())));
          }
        } catch (err) {
          req.log.warn({ err, runId }, 'cancelVisualisation send failed after timeout');
        }
        return reply.code(504).send({
          schema: FAILED_SCHEMA_VERSION,
          runId,
          error: 'visualisation_failed',
          code: 'start_timeout',
          message: `start exceeded ${START_TIMEOUT_MS}ms`,
        });
      }

      return reply.code(502).send({
        schema: FAILED_SCHEMA_VERSION,
        runId,
        error: 'visualisation_failed',
        code: f.code,
        message: f.message,
      });
    }

    // Happy path: build the portal contract response. The agent gave
    // us the local Cirrus URL in `readyEvent.signallingUrl` — we
    // intentionally do not surface that to the caller; the portal
    // talks to PRISM's server-side proxy.
    const signallingUrl = buildSignallingUrl(runId);
    const playerUrl = buildPlayerUrl(runId);
    const turn = generateTurnCredential({ runId });

    await db
      .update(visualiserRuns)
      .set({
        status: 'streaming',
        signallingUrl,
        playerUrl,
        streamerId: readyEvent.streamerId ?? null,
        readyAt: new Date(),
        updatedAt: new Date(),
      })
      .where(eq(visualiserRuns.id, runId));

    if (!turn) {
      req.log.warn({ runId }, 'TURN_SECRET unset; returning turn: null sentinel (Phase H wires the real secret)');
    }

    return reply.send({
      schema: READY_SCHEMA_VERSION,
      runId,
      status: 'streaming',
      signallingUrl,
      playerUrl,
      streamerId: readyEvent.streamerId,
      turn,
    });
  });

  /* ---------- GET /api/visualiser/streams ---------- */
  app.get<{ Querystring: unknown }>('/streams', {
    preHandler: requireAuth,
  }, async (req, reply) => {
    const parsed = listQuery.safeParse(req.query);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid query', issues: parsed.error.issues });
    const filterStatuses = parsed.data.status
      ? parsed.data.status.split(',').map((s) => s.trim()).filter(Boolean)
      : null;
    const whereClause = filterStatuses && filterStatuses.length > 0
      ? inArray(visualiserRuns.status, filterStatuses)
      : undefined;
    const rows = await db
      .select()
      .from(visualiserRuns)
      .where(whereClause)
      .orderBy(desc(visualiserRuns.createdAt))
      .limit(parsed.data.limit)
      .offset(parsed.data.offset);
    return { runs: rows.map((row) => toPublicRun(row)), limit: parsed.data.limit, offset: parsed.data.offset };
  });

  /* ---------- GET /api/visualiser/streams/:runId ---------- */
  app.get<{ Params: { runId: string } }>('/streams/:runId', {
    preHandler: requireAuth,
  }, async (req, reply) => {
    const row = await db.query.visualiserRuns.findFirst({ where: eq(visualiserRuns.id, req.params.runId) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    // Phase I: include a freshly-minted TURN bundle so the admin viewer
    // can wire it into the browser RTCPeerConnection. See toPublicRun
    // for the rationale on why we only mint here and not on the list
    // endpoint.
    return toPublicRun(row, { withTurn: true });
  });

  /* ---------- DELETE /api/visualiser/streams/:runId ---------- */
  app.delete<{ Params: { runId: string } }>('/streams/:runId', {
    preHandler: requireAuth,
  }, async (req, reply) => {
    const row = await db.query.visualiserRuns.findFirst({ where: eq(visualiserRuns.id, req.params.runId) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (!(await ownerCanCancel(row, req))) return reply.code(403).send({ error: 'forbidden' });
    if (row.status === 'ended' || row.status === 'failed') {
      return reply.code(409).send({ error: `run is already ${row.status}` });
    }

    // Best-effort: send cancelVisualisation to the agent. The agent
    // emits `visualisationEnded` when the orchestrator exits; that
    // hits the WS handler and finalises the row. We optimistically
    // set status=ended here so the admin SPA reflects the click
    // immediately even if the agent is offline.
    if (row.agentSessionId) {
      const conn = sessionRegistry.getAgent(row.agentSessionId);
      if (conn && conn.socket.readyState === conn.socket.OPEN) {
        const cancel: CancelVisualisationData = { runId: row.id, reason: 'cancelled by operator' };
        try {
          conn.socket.send(JSON.stringify(envelope('cancelVisualisation', cancel, randomUUID())));
        } catch (err) {
          req.log.warn({ err, runId: row.id }, 'cancelVisualisation send failed');
        }
      }
    }
    await db
      .update(visualiserRuns)
      .set({ status: 'ended', endedAt: new Date(), updatedAt: new Date() })
      .where(eq(visualiserRuns.id, row.id));
    if (row.workstationId) {
      await releaseVisualiserSlot(row.workstationId).catch(() => null);
      broadcastWorkstationUpdate({ id: row.workstationId, visualiserRunEnded: row.id });
    }
    return { ok: true };
  });

  /* ---------- POST /api/visualiser/streams/:runId/signalling-token ---------- */
  app.post<{ Params: { runId: string } }>('/streams/:runId/signalling-token', {
    preHandler: requireAuth,
  }, async (req, reply) => {
    const row = await db.query.visualiserRuns.findFirst({ where: eq(visualiserRuns.id, req.params.runId) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (!(await ownerCanCancel(row, req))) return reply.code(403).send({ error: 'forbidden' });
    if (row.status !== 'streaming' && row.status !== 'importing') {
      return reply.code(409).send({ error: `run is ${row.status}` });
    }
    try {
      const subject = req.principal?.kind === 'apiKey' ? req.principal.apiKeyId
                    : req.principal?.kind === 'adminSession' ? `admin:${req.principal.username}`
                    : undefined;
      const { token, exp } = issueSignallingToken({ runId: row.id, subject });
      return { token, exp };
    } catch (err) {
      const msg = (err as Error).message;
      req.log.error({ err, runId: row.id }, 'failed to mint signalling token');
      return reply.code(503).send({ error: 'signalling_token_unavailable', message: msg });
    }
  });

  /* ---------- GET /api/visualiser/workstations ---------- */
  app.get('/workstations', {
    preHandler: requireAdmin,
  }, async () => {
    // Returns every can_visualise workstation, online OR offline, so
    // the admin UI can show the full pool and grey out offline rows.
    const wsRows = await db
      .select()
      .from(workstations)
      .where(eq(workstations.canVisualise, true))
      .orderBy(desc(workstations.lastSeenAt));
    const sessions = await db.select().from(agentSessions);
    const sessByWs = new Map<string, typeof sessions[number][]>();
    for (const s of sessions) {
      const arr = sessByWs.get(s.workstationId) ?? [];
      arr.push(s);
      sessByWs.set(s.workstationId, arr);
    }
    return {
      workstations: wsRows.map((w) => ({
        id: w.id,
        nodeName: w.nodeName,
        machineId: w.machineId,
        canVisualise: w.canVisualise,
        currentVisualiserLoad: w.currentVisualiserLoad,
        slotsTotal: w.slotsTotal,
        agentVersion: w.agentVersion,
        online: (sessByWs.get(w.id) ?? []).length > 0,
      })),
    };
  });

};

export default plugin;
