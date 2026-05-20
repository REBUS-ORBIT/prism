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
import { agentSessions, workstations } from '../db/schema.js';
import { requireAdmin } from '../auth/middleware.js';

const updateBody = z.object({
  nodeName:   z.string().min(1).max(128).optional(),
  canConvert: z.boolean().optional(),
  canLayer:   z.boolean().optional(),
  canReceive: z.boolean().optional(),
  isEnabled:  z.boolean().optional(),
  notes:      z.string().nullable().optional(),
});

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
      })),
    };
  });

  app.get<{ Params: { id: string } }>('/:id', async (req, reply) => {
    const row = await db.query.workstations.findFirst({ where: eq(workstations.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    return row;
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
};

export default plugin;
