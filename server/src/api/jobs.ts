/**
 * /api/jobs — list, get, delete, stream logs.
 *
 * Phase 1 implements list / get / delete against the DB and a basic
 * polling-friendly /:id endpoint. SSE log streaming is added when the
 * agent WS actually pushes logs in Phase 2.
 */
import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import type { FastifyPluginAsync } from 'fastify';
import { and, asc, desc, eq, or } from 'drizzle-orm';
import { z } from 'zod';
import { db } from '../db/client.js';
import { jobLogs, jobs } from '../db/schema.js';
import { requireAuth } from '../auth/middleware.js';
import { sessionRegistry } from '../ws/sessionRegistry.js';
import { envelope } from '../../../shared/contracts/agent-protocol.js';
import { broadcastJobUpdate } from '../ws/adminProtocol.js';
import { enqueueConvert } from '../jobs/queue.js';

const ALLOWED_OUTPUT_FORMATS = new Set(['3dm', 'step', 'ifc', 'glb']);

const plugin: FastifyPluginAsync = async (app) => {
  app.addHook('preHandler', requireAuth);

  // GET /api/jobs?status=&limit=&offset=
  app.get<{ Querystring: { status?: string; limit?: string; offset?: string } }>('/', async (req) => {
    const limit = Math.min(Math.max(Number(req.query.limit ?? 50), 1), 500);
    const offset = Math.max(Number(req.query.offset ?? 0), 0);
    const whereClause = req.query.status ? eq(jobs.status, req.query.status) : undefined;
    const rows = await db
      .select()
      .from(jobs)
      .where(whereClause)
      .orderBy(desc(jobs.createdAt))
      .limit(limit)
      .offset(offset);
    return { jobs: rows.map(toPublicJob), limit, offset };
  });

  // GET /api/jobs/:id
  app.get<{ Params: { id: string } }>('/:id', async (req, reply) => {
    const row = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    return toPublicJob(row);
  });

  // DELETE /api/jobs/:id
  app.delete<{ Params: { id: string } }>('/:id', async (req, reply) => {
    const res = await db.delete(jobs).where(eq(jobs.id, req.params.id)).returning({ id: jobs.id });
    if (res.length === 0) return reply.code(404).send({ error: 'not found' });
    return { deleted: res[0]!.id };
  });

  // POST /api/jobs/:id/cancel
  app.post<{ Params: { id: string } }>('/:id/cancel', async (req, reply) => {
    const CANCELLABLE = or(
      eq(jobs.status, 'queued'),
      eq(jobs.status, 'dispatched'),
      eq(jobs.status, 'processing'),
      eq(jobs.status, 'uploading'),
    );
    // Atomic check-and-update: only succeeds if the job is still in a
    // cancellable state, preventing races with the agent completing the job.
    const updated = await db
      .update(jobs)
      .set({ status: 'cancelled', updatedAt: new Date() })
      .where(and(eq(jobs.id, req.params.id), CANCELLABLE))
      .returning({ id: jobs.id, agentSessionId: jobs.agentSessionId });

    if (updated.length === 0) {
      // Job either doesn't exist or is already in a terminal state.
      const existing = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
      if (!existing) return reply.code(404).send({ error: 'not found' });
      return reply.code(409).send({ error: `job is already ${existing.status}` });
    }

    const { agentSessionId } = updated[0]!;
    // Forward cancel to the agent if it has the job in flight.
    if (agentSessionId) {
      const conn = sessionRegistry.getAgent(agentSessionId);
      if (conn && conn.socket.readyState === conn.socket.OPEN) {
        conn.socket.send(JSON.stringify(envelope('cancel', { jobId: req.params.id, reason: 'cancelled by admin' })));
      }
    }

    broadcastJobUpdate(req.params.id, { status: 'cancelled' });
    return { cancelled: true };
  });

  // GET /api/jobs/:id/logs?since=
  app.get<{ Params: { id: string }; Querystring: { since?: string } }>('/:id/logs', async (req, reply) => {
    const job = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!job) return reply.code(404).send({ error: 'not found' });
    const sinceId = req.query.since ? Number(req.query.since) : 0;
    // Drizzle: cursor on id is monotonic. Simple paginate by id > since.
    const lines = await db
      .select()
      .from(jobLogs)
      .where(
        sinceId > 0
          ? and(eq(jobLogs.jobId, req.params.id), /* gt: */ undefined)
          : eq(jobLogs.jobId, req.params.id)
      )
      .orderBy(asc(jobLogs.id))
      .limit(2000);
    return { logs: lines };
  });

  // ---------------------------------------------------------------- layers
  // Two-phase pollLayers / convert flow:
  //   1. Caller submits with selectLayers=true.
  //   2. PRISM dispatches a pollLayers job; agent returns the layer tree.
  //   3. Job status becomes `awaiting_selection`; layers visible here.
  //   4. Caller POSTs the chosen subset; PRISM re-queues for convert.

  // GET /api/jobs/:id/layers — returns the cached layer tree (404 if none yet).
  app.get<{ Params: { id: string } }>('/:id/layers', async (req, reply) => {
    const row = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (!row.layersJson) {
      return reply.code(404).send({
        error: 'layers not available yet',
        status: row.status,
        selectLayers: row.selectLayers,
      });
    }
    return {
      jobId: row.id,
      status: row.status,
      layers: row.layersJson,
      includedLayers: row.includedLayers ?? [],
      includeLayerDescendants: row.includeLayerDescendants,
    };
  });

  const selectSchema = z.object({
    includedLayers: z.array(z.string()).default([]),
    includeLayerDescendants: z.boolean().default(false),
  });

  // POST /api/jobs/:id/layers — submit the user's selection. The job must be
  // in `awaiting_selection`; we persist the selection and re-enqueue the job
  // for normal convert dispatch.
  app.post<{ Params: { id: string } }>('/:id/layers', async (req, reply) => {
    const parsed = selectSchema.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body', issues: parsed.error.issues });

    const row = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (row.status !== 'awaiting_selection') {
      return reply.code(409).send({ error: `job is ${row.status}, not awaiting_selection` });
    }

    // Merge into the persisted options blob so the agent receives the
    // selection in the AssignData.options payload exactly like a direct
    // single-phase submit would.
    const options = (row.options as Record<string, unknown> | null) ?? {};
    options['includedLayers'] = parsed.data.includedLayers;
    options['includeLayerDescendants'] = parsed.data.includeLayerDescendants;

    await db
      .update(jobs)
      .set({
        status: 'queued',
        includedLayers: parsed.data.includedLayers,
        includeLayerDescendants: parsed.data.includeLayerDescendants,
        options,
        currentStage: 'queued',
        lastMessage: 'awaiting convert dispatch',
        updatedAt: new Date(),
      })
      .where(eq(jobs.id, req.params.id));

    broadcastJobUpdate(req.params.id, {
      status: 'queued',
      currentStage: 'queued',
      lastMessage: 'awaiting convert dispatch',
      includedLayers: parsed.data.includedLayers,
      includeLayerDescendants: parsed.data.includeLayerDescendants,
    });

    await enqueueConvert({ jobId: req.params.id });

    return {
      jobId: req.params.id,
      status: 'queued',
      includedLayers: parsed.data.includedLayers,
      includeLayerDescendants: parsed.data.includeLayerDescendants,
    };
  });

  // GET /api/jobs/:id/outputs/:format
  // Streams a non-ORBIT output file (3DM / GLB / IFC / STEP) produced by the agent.
  app.get<{ Params: { id: string; format: string } }>('/:id/outputs/:format', async (req, reply) => {
    const fmt = req.params.format.toLowerCase();
    if (!ALLOWED_OUTPUT_FORMATS.has(fmt)) return reply.code(400).send({ error: 'unknown format' });
    const job = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!job) return reply.code(404).send({ error: 'not found' });
    const stageRoot = resolve(process.env.UPLOAD_DIR ?? './uploads');
    const outPath = join(stageRoot, 'outputs', req.params.id, fmt);
    try {
      const s = await stat(outPath);
      reply
        .header('content-type', 'application/octet-stream')
        .header('content-length', String(s.size))
        .header('content-disposition', `attachment; filename="${encodeURIComponent(job.fileName)}"`);
      return reply.send(createReadStream(outPath));
    } catch {
      return reply.code(404).send({ error: 'output not available' });
    }
  });
};

function toPublicJob(row: typeof jobs.$inferSelect) {
  return {
    id: row.id,
    status: row.status,
    createdAt: row.createdAt,
    updatedAt: row.updatedAt,
    completedAt: row.completedAt,
    fileName: row.fileName,
    fileSize: row.fileSize,
    format: row.format,
    orbitTarget: row.orbitTarget,
    projectId: row.projectId,
    modelId: row.modelId,
    modelName: row.modelName,
    nodeName: row.nodeName,
    currentStage: row.currentStage,
    progressPercent: row.progressPercent,
    lastMessage: row.lastMessage,
    resultUrl: row.resultUrl,
    rootObjectId: row.rootObjectId,
    versionId: row.versionId,
    jobType: row.jobType,
    outputFormats: row.outputFormats,
    outputs: row.outputs,
    receiveVersionId: row.receiveVersionId,
    error: row.error,
    selectLayers: row.selectLayers,
    includedLayers: row.includedLayers ?? [],
    includeLayerDescendants: row.includeLayerDescendants,
    hasLayers: !!row.layersJson,
  };
}

export default plugin;
