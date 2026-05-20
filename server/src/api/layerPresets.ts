/**
 * /api/layer-presets — per (project_id, model_name) saved layer pick.
 */
import type { FastifyPluginAsync } from 'fastify';
import { and, eq } from 'drizzle-orm';
import { z } from 'zod';
import { db } from '../db/client.js';
import { layerPresets } from '../db/schema.js';
import { requireAuth } from '../auth/middleware.js';

const upsertBody = z.object({
  projectId: z.string().min(1),
  modelName: z.string().min(1),
  includedLayers: z.array(z.string()),
  knownLayers: z.array(z.string()).optional(),
  includeDescendants: z.boolean().optional(),
});

const plugin: FastifyPluginAsync = async (app) => {
  app.addHook('preHandler', requireAuth);

  // GET /api/layer-presets?projectId=&modelName=
  app.get<{ Querystring: { projectId?: string; modelName?: string } }>('/', async (req, reply) => {
    if (!req.query.projectId || !req.query.modelName) {
      return reply.code(400).send({ error: 'projectId and modelName required' });
    }
    const row = await db.query.layerPresets.findFirst({
      where: and(
        eq(layerPresets.projectId, req.query.projectId),
        eq(layerPresets.modelName, req.query.modelName),
      ),
    });
    return row ?? null;
  });

  // PUT /api/layer-presets
  app.put('/', async (req, reply) => {
    const parsed = upsertBody.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body' });

    const existing = await db.query.layerPresets.findFirst({
      where: and(
        eq(layerPresets.projectId, parsed.data.projectId),
        eq(layerPresets.modelName, parsed.data.modelName),
      ),
    });

    if (existing) {
      const updated = await db
        .update(layerPresets)
        .set({
          includedLayers:    parsed.data.includedLayers,
          knownLayers:       parsed.data.knownLayers ?? existing.knownLayers,
          includeDescendants: parsed.data.includeDescendants ?? existing.includeDescendants,
          updatedAt: new Date(),
        })
        .where(eq(layerPresets.id, existing.id))
        .returning();
      return updated[0];
    }

    const inserted = await db
      .insert(layerPresets)
      .values({
        projectId: parsed.data.projectId,
        modelName: parsed.data.modelName,
        includedLayers: parsed.data.includedLayers,
        knownLayers: parsed.data.knownLayers ?? [],
        includeDescendants: parsed.data.includeDescendants ?? true,
      })
      .returning();
    return reply.code(201).send(inserted[0]);
  });
};

export default plugin;
