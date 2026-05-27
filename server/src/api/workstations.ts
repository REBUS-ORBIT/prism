/**
 * /api/workstations — admin CRUD over the persistent workstation pool.
 *
 * The live status (online/busy + slot count) is joined from
 * `agent_sessions`, which the WS gateway maintains.
 */
import type { FastifyPluginAsync } from 'fastify';
import { z } from 'zod';
import { eq, desc } from 'drizzle-orm';
import { db } from '../db/client.js';
import { agentSessions, workstations, type AgentSession } from '../db/schema.js';
import { requireAdmin } from '../auth/middleware.js';
import { sendRestartToAgent, sendUpdateToAgent } from '../ws/agentProtocol.js';

const updateBody = z.object({
  nodeName:     z.string().min(1).max(128).optional(),
  canConvert:   z.boolean().optional(),
  canLayer:     z.boolean().optional(),
  canReceive:   z.boolean().optional(),
  canVisualise: z.boolean().optional(),
  isEnabled:    z.boolean().optional(),
  notes:        z.string().nullable().optional(),
});

/**
 * Pick the most useful `remote_addr` out of a workstation's session
 * list. Heartbeat-then-connect ordering means a live agent (which has
 * been emitting heartbeats every 15s) wins over a stale row whose
 * socket never cleanly closed, and the per-machineId de-dup in the WS
 * gateway means we usually only have one row anyway.
 *
 * Returns null when no session exists at all — `agent_sessions` rows
 * are deleted on socket close so an offline workstation has no IP to
 * surface here. (The admin SPA falls back to the legacy
 * `nodeName.dnsSuffix` URL in that case via `workstationUrl.ts`.)
 */
function pickHost(sessions: AgentSession[] | undefined): string | null {
  if (!sessions || sessions.length === 0) return null;
  const sorted = [...sessions].sort((a, b) => {
    const ta = (a.lastHeartbeat ?? a.connectedAt ?? new Date(0)).valueOf();
    const tb = (b.lastHeartbeat ?? b.connectedAt ?? new Date(0)).valueOf();
    return tb - ta;
  });
  for (const s of sorted) {
    if (s.remoteAddr && s.remoteAddr.trim().length > 0) return s.remoteAddr;
  }
  return null;
}

const plugin: FastifyPluginAsync = async (app) => {
  app.addHook('preHandler', requireAdmin);

  app.get('/', async () => {
    const rows = await db.select().from(workstations).orderBy(desc(workstations.lastSeenAt));
    // Join sessions in code (small table). Returns live online state per machine.
    const sessions = await db.select().from(agentSessions);
    const sessByWs = new Map<string, typeof sessions[number][]>();
    for (const s of sessions) {
      const arr = sessByWs.get(s.workstationId) ?? [];
      arr.push(s);
      sessByWs.set(s.workstationId, arr);
    }
    return {
      workstations: rows.map((w) => ({
        ...w,
        online: (sessByWs.get(w.id) ?? []).length > 0,
        slotsBusy: (sessByWs.get(w.id) ?? []).reduce((acc, s) => acc + s.slotsBusy, 0),
        sessions: (sessByWs.get(w.id) ?? []).length,
        host: pickHost(sessByWs.get(w.id)),
      })),
    };
  });

  app.get<{ Params: { id: string } }>('/:id', async (req, reply) => {
    const row = await db.query.workstations.findFirst({ where: eq(workstations.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    const sessions = await db
      .select()
      .from(agentSessions)
      .where(eq(agentSessions.workstationId, row.id));
    return { ...row, host: pickHost(sessions) };
  });

  app.patch<{ Params: { id: string }; Body: unknown }>('/:id', async (req, reply) => {
    const body = updateBody.safeParse(req.body);
    if (!body.success) return reply.code(400).send({ error: 'invalid body', issues: body.error.issues });
    const res = await db
      .update(workstations)
      .set({ ...body.data })
      .where(eq(workstations.id, req.params.id))
      .returning();
    if (res.length === 0) return reply.code(404).send({ error: 'not found' });
    return res[0];
  });

  // Workstation rows are otherwise only inserted by the WS gateway when an
  // agent calls `hello`. Admin can delete a stale row here.
  app.delete<{ Params: { id: string } }>('/:id', async (req, reply) => {
    const res = await db.delete(workstations).where(eq(workstations.id, req.params.id)).returning({ id: workstations.id });
    if (res.length === 0) return reply.code(404).send({ error: 'not found' });
    return { deleted: res[0]!.id };
  });

  // ------------------------------------------------------------------ lifecycle
  // Both routes look up the workstation by id, confirm an active agent
  // session exists in the in-memory registry (keyed off machineId), and
  // dispatch the WS envelope. The agent acks the action by either
  // disconnecting (restart) or completing the download (update).

  /**
   * POST /:id/restart — ask the agent to cleanly exit. The Windows
   * Scheduled Task + a self-spawned PowerShell helper script
   * relaunch the agent within ~1 minute.
   */
  app.post<{ Params: { id: string }; Body: { reason?: string } | undefined }>(
    '/:id/restart',
    async (req, reply) => {
      const row = await db.query.workstations.findFirst({ where: eq(workstations.id, req.params.id) });
      if (!row) return reply.code(404).send({ error: 'workstation not found' });
      const reason = typeof req.body?.reason === 'string' ? req.body.reason : undefined;
      const sent = sendRestartToAgent(row.machineId, reason ? { reason } : {});
      if (!sent) return reply.code(503).send({ error: 'agent not connected' });
      req.log.info({ workstationId: row.id, nodeName: row.nodeName, machineId: row.machineId, reason }, 'restart dispatched to agent');
      return { queued: true };
    },
  );

  /**
   * POST /:id/update — ask the agent to check GitHub Releases and apply
   * a new build if one is available. Optional `{tag: "v0.1.33"}` pins a
   * specific release; default is the latest.
   *
   * Older agents (pre-v0.1.33) silently ignore unknown message types,
   * so this returns 503 only when no agent is connected at all.
   */
  app.post<{ Params: { id: string }; Body: { tag?: string } | undefined }>(
    '/:id/update',
    async (req, reply) => {
      const row = await db.query.workstations.findFirst({ where: eq(workstations.id, req.params.id) });
      if (!row) return reply.code(404).send({ error: 'workstation not found' });
      const tag = typeof req.body?.tag === 'string' && req.body.tag.trim().length > 0
        ? req.body.tag.trim()
        : undefined;
      const sent = sendUpdateToAgent(row.machineId, tag ? { tag } : {});
      if (!sent) return reply.code(503).send({ error: 'agent not connected' });
      req.log.info({ workstationId: row.id, nodeName: row.nodeName, machineId: row.machineId, tag }, 'update dispatched to agent');
      return { queued: true };
    },
  );
};

export default plugin;
