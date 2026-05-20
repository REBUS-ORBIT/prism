/**
 * /api/keys — admin manages API keys for external /v1/* callers.
 */
import type { FastifyPluginAsync } from 'fastify';
import { z } from 'zod';
import { desc, eq } from 'drizzle-orm';
import { db } from '../db/client.js';
import { apiKeys } from '../db/schema.js';
import { mintApiKey } from '../auth/apiKey.js';
import { requireAdmin } from '../auth/middleware.js';

const createBody = z.object({
  name: z.string().min(1).max(128),
  rateLimitPerMin: z.number().int().positive().optional(),
  monthlyQuota: z.number().int().positive().optional(),
});

const plugin: FastifyPluginAsync = async (app) => {
  app.addHook('preHandler', requireAdmin);

  app.get('/', async () => {
    const rows = await db.select().from(apiKeys).orderBy(desc(apiKeys.createdAt));
    return { keys: rows.map((r) => ({
      id: r.id,
      name: r.name,
      isActive: r.isActive,
      rateLimitPerMin: r.rateLimitPerMin,
      monthlyQuota: r.monthlyQuota,
      createdAt: r.createdAt,
      lastUsedAt: r.lastUsedAt,
    })) };
  });

  // POST /api/keys -> { plaintext, ...row }
  // The plaintext is shown to the admin once and never again.
  app.post('/', async (req, reply) => {
    const parsed = createBody.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body', issues: parsed.error.issues });
    const { plaintext, hash } = mintApiKey();
    const inserted = await db
      .insert(apiKeys)
      .values({
        name: parsed.data.name,
        keyHash: hash,
        rateLimitPerMin: parsed.data.rateLimitPerMin ?? null,
        monthlyQuota: parsed.data.monthlyQuota ?? null,
      })
      .returning();
    const row = inserted[0]!;
    return reply.code(201).send({
      plaintext,
      key: {
        id: row.id,
        name: row.name,
        isActive: row.isActive,
        rateLimitPerMin: row.rateLimitPerMin,
        monthlyQuota: row.monthlyQuota,
        createdAt: row.createdAt,
      },
    });
  });

  app.delete<{ Params: { id: string } }>('/:id', async (req, reply) => {
    const res = await db.delete(apiKeys).where(eq(apiKeys.id, req.params.id)).returning({ id: apiKeys.id });
    if (res.length === 0) return reply.code(404).send({ error: 'not found' });
    return { deleted: res[0]!.id };
  });

  app.patch<{ Params: { id: string }; Body: { isActive?: boolean } }>('/:id', async (req, reply) => {
    const body = z.object({ isActive: z.boolean().optional() }).safeParse(req.body);
    if (!body.success) return reply.code(400).send({ error: 'invalid body' });
    const updated = await db
      .update(apiKeys)
      .set({ ...body.data })
      .where(eq(apiKeys.id, req.params.id))
      .returning();
    if (updated.length === 0) return reply.code(404).send({ error: 'not found' });
    return { ok: true };
  });
};

export default plugin;
