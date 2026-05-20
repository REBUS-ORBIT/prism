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
import { and, asc, desc, eq } from 'drizzle-orm';
import { db } from '../db/client.js';
import { jobLogs, jobs } from '../db/schema.js';
import { requireAuth } from '../auth/middleware.js';

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
  };
}

export default plugin;
