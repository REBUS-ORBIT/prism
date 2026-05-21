/**
 * Public versioned external API at /v1/*.
 *
 * Auth: X-API-Key only. No admin sessions, no ORBIT bearer pass-through.
 * Every request is gated by per-key rate limit; every job submission
 * also consumes the per-key monthly quota.
 *
 * Endpoints (all return JSON, all errors return { error: string }):
 *   GET  /v1/health
 *   POST /v1/convert/async        multipart/form-data
 *   POST /v1/receive/async        application/json
 *   GET  /v1/jobs/:id
 *   GET  /v1/jobs/:id/stream      text/event-stream
 *   GET  /v1/jobs/:id/outputs/:format
 *
 * The implementations delegate to existing handlers where possible
 * rather than duplicating logic; this keeps /api and /v1 in lockstep.
 */
import { createReadStream } from 'node:fs';
import { mkdir, stat, writeFile } from 'node:fs/promises';
import { extname, join, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import type { FastifyPluginAsync } from 'fastify';
import { eq } from 'drizzle-orm';
import { z } from 'zod';
import { db } from '../db/client.js';
import { jobs } from '../db/schema.js';
import { convertQueue, enqueueConvert } from '../jobs/queue.js';
import { requireApiKey } from '../auth/apiKey.js';
import { consumeQuotaOrReject, enforceRateLimit } from './rateLimit.js';

const UPLOAD_DIR = process.env.UPLOAD_DIR ?? '/var/lib/prism/uploads';

const SUPPORTED_EXTS = new Set([
  '.3dm', '.dwg', '.dxf', '.fbx', '.obj', '.stl', '.ply', '.3mf', '.dae', '.step', '.iges', '.igs', '.stp',
]);
const ALLOWED_OUTPUT_FORMATS = new Set(['3dm', 'step', 'ifc', 'glb']);
const RECEIVE_OUTPUTS = new Set(['3dm', 'step']);

const convertFieldsSchema = z.object({
  projectId:   z.string().min(1),
  modelId:     z.string().min(1),
  modelName:   z.string().optional(),
  orbitTarget: z.enum(['prod', 'dev']).default('prod'),
  swapYZ:      z.coerce.boolean().optional(),
  quality:     z.enum(['sensible', 'extreme']).optional(),
  callbackUrl: z.string().url().optional(),
  outputFormats: z.string().optional(),    // CSV
  includedLayers: z.string().optional(),    // CSV
  includeLayerDescendants: z.coerce.boolean().optional(),
  // See /api/convert/async — selectLayers=true puts the job into the
  // two-phase pollLayers → awaiting_selection → convert flow.
  selectLayers: z.coerce.boolean().optional(),
});

const receiveBodySchema = z.object({
  projectId: z.string().min(1),
  modelId:   z.string().min(1),
  versionId: z.string().min(1),
  modelName: z.string().optional(),
  orbitTarget: z.enum(['prod', 'dev']).optional(),
  outputFormat: z.enum(['3dm', 'step']).optional(),
  callbackUrl:  z.string().url().optional(),
});

const plugin: FastifyPluginAsync = async (app) => {
  await mkdir(UPLOAD_DIR, { recursive: true }).catch(() => undefined);

  // Every /v1 route requires a valid API key + rate-limit check.
  app.addHook('preHandler', requireApiKey);
  app.addHook('preHandler', enforceRateLimit);

  // ---------------------------------------------------------------------- meta
  app.get('/health', async () => ({ status: 'ok', api: 'v1' }));

  // ------------------------------------------------------------------ convert
  app.post('/convert/async', async (req, reply) => {
    if (!req.isMultipart()) return reply.code(415).send({ error: 'multipart/form-data required' });

    const parts = req.parts();
    const fields: Record<string, string> = {};
    let fileName = '';
    let savedPath = '';
    let fileSize = 0;

    for await (const part of parts) {
      if (part.type === 'file') {
        fileName = part.filename;
        const ext = extname(fileName).toLowerCase();
        if (!SUPPORTED_EXTS.has(ext)) return reply.code(415).send({ error: `unsupported format: ${ext}` });
        const id = randomUUID();
        savedPath = resolve(join(UPLOAD_DIR, `${id}${ext}`));
        const chunks: Buffer[] = [];
        for await (const chunk of part.file) chunks.push(chunk as Buffer);
        const buf = Buffer.concat(chunks);
        await writeFile(savedPath, buf);
        fileSize = buf.length;
      } else {
        fields[part.fieldname] = String(part.value ?? '');
      }
    }
    if (!savedPath) return reply.code(400).send({ error: 'file part missing' });

    const parsed = convertFieldsSchema.safeParse(fields);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid fields', issues: parsed.error.issues });

    // Quota check (after upload to keep error response cheap)
    if (!(await consumeQuotaOrReject(req, reply))) return;

    const outputFormats = parsed.data.outputFormats
      ? parsed.data.outputFormats.split(',').map((s) => s.trim().toLowerCase()).filter((s) => ALLOWED_OUTPUT_FORMATS.has(s))
      : [];

    const preSelectedLayers = parsed.data.includedLayers
      ? parsed.data.includedLayers.split(',').map((s) => s.trim()).filter(Boolean)
      : [];
    const includeLayerDescendants = parsed.data.includeLayerDescendants ?? false;
    const selectLayers = !!parsed.data.selectLayers;

    const options = {
      swapYZ: !!parsed.data.swapYZ,
      quality: parsed.data.quality ?? 'sensible',
      includedLayers: preSelectedLayers,
      includeLayerDescendants,
    };

    const [row] = await db.insert(jobs).values({
      jobType: 'convert',
      format: extname(fileName).toLowerCase(),
      fileName, fileSize, filePath: savedPath,
      orbitTarget: parsed.data.orbitTarget,
      projectId: parsed.data.projectId,
      modelId: parsed.data.modelId,
      modelName: parsed.data.modelName,
      outputFormats,
      options,
      selectLayers,
      includedLayers: preSelectedLayers.length ? preSelectedLayers : null,
      includeLayerDescendants,
      callbackUrl: parsed.data.callbackUrl,
      submittedBy: `apikey:${apiKeyIdOf(req) ?? 'unknown'}`,
    }).returning();
    if (!row) return reply.code(500).send({ error: 'failed to persist job' });

    await enqueueConvert({ jobId: row.id });
    return reply.code(202).send({ jobId: row.id, status: row.status });
  });

  // ------------------------------------------------------------------ receive
  app.post('/receive/async', async (req, reply) => {
    const parsed = receiveBodySchema.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body', issues: parsed.error.issues });
    if (!(await consumeQuotaOrReject(req, reply))) return;

    const out = (parsed.data.outputFormat ?? '3dm').toLowerCase();
    if (!RECEIVE_OUTPUTS.has(out)) return reply.code(400).send({ error: `outputFormat must be one of ${[...RECEIVE_OUTPUTS].join(', ')}` });

    const stageDir = join(resolve(UPLOAD_DIR), 'outputs');
    await mkdir(stageDir, { recursive: true });

    const [row] = await db.insert(jobs).values({
      jobType: 'receive',
      format: out,
      fileName: `${parsed.data.modelName ?? parsed.data.modelId}-${parsed.data.versionId.slice(0, 8)}.${out}`,
      fileSize: 0,
      filePath: stageDir,
      orbitTarget: parsed.data.orbitTarget ?? 'prod',
      projectId: parsed.data.projectId,
      modelId: parsed.data.modelId,
      modelName: parsed.data.modelName,
      receiveVersionId: parsed.data.versionId,
      outputFormats: [out],
      callbackUrl: parsed.data.callbackUrl,
      submittedBy: `apikey:${apiKeyIdOf(req) ?? 'unknown'}`,
    }).returning();
    if (!row) return reply.code(500).send({ error: 'failed to persist job' });

    await convertQueue.add('receive', { jobId: row.id }, { removeOnComplete: 100, removeOnFail: 100 });
    return reply.code(202).send({ jobId: row.id, status: row.status });
  });

  // --------------------------------------------------------------------- jobs
  app.get<{ Params: { id: string } }>('/jobs/:id', async (req, reply) => {
    const row = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (!isOwnedByCurrentKey(row, req)) return reply.code(403).send({ error: 'forbidden' });
    return toPublic(row);
  });

  // -------------------------------------------------------------- layers
  app.get<{ Params: { id: string } }>('/jobs/:id/layers', async (req, reply) => {
    const row = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (!isOwnedByCurrentKey(row, req)) return reply.code(403).send({ error: 'forbidden' });
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

  const layerSelectSchema = z.object({
    includedLayers: z.array(z.string()).default([]),
    includeLayerDescendants: z.boolean().default(false),
  });

  app.post<{ Params: { id: string } }>('/jobs/:id/layers', async (req, reply) => {
    const parsed = layerSelectSchema.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body', issues: parsed.error.issues });
    const row = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (!isOwnedByCurrentKey(row, req)) return reply.code(403).send({ error: 'forbidden' });
    if (row.status !== 'awaiting_selection') {
      return reply.code(409).send({ error: `job is ${row.status}, not awaiting_selection` });
    }

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

    await enqueueConvert({ jobId: row.id });

    return {
      jobId: row.id,
      status: 'queued',
      includedLayers: parsed.data.includedLayers,
      includeLayerDescendants: parsed.data.includeLayerDescendants,
    };
  });

  app.get<{ Params: { id: string; format: string } }>('/jobs/:id/outputs/:format', async (req, reply) => {
    const row = await db.query.jobs.findFirst({ where: eq(jobs.id, req.params.id) });
    if (!row) return reply.code(404).send({ error: 'not found' });
    if (!isOwnedByCurrentKey(row, req)) return reply.code(403).send({ error: 'forbidden' });

    const fmt = req.params.format.toLowerCase();
    if (!ALLOWED_OUTPUT_FORMATS.has(fmt)) return reply.code(400).send({ error: 'unknown format' });
    const outPath = join(resolve(UPLOAD_DIR), 'outputs', req.params.id, fmt);
    try {
      const s = await stat(outPath);
      reply
        .header('content-type', 'application/octet-stream')
        .header('content-length', String(s.size))
        .header('content-disposition', `attachment; filename="${encodeURIComponent(row.fileName)}"`);
      return reply.send(createReadStream(outPath));
    } catch {
      return reply.code(404).send({ error: 'output not available' });
    }
  });

  // ---------------------------------------------------------- webhook helper
  app.get('/webhooks/signature-spec', async () => ({
    header: 'x-prism-signature',
    algorithm: 'HMAC-SHA256',
    encoding: 'sha256=<hex>',
    payload: 'raw request body bytes',
  }));
};

function apiKeyIdOf(req: { principal?: { kind: string } }): string | undefined {
  const p = req.principal as { kind: string; apiKeyId?: string } | undefined;
  return p?.kind === 'apiKey' ? p.apiKeyId : undefined;
}

function isOwnedByCurrentKey(row: typeof jobs.$inferSelect, req: { principal?: { kind: string } }): boolean {
  const id = apiKeyIdOf(req);
  if (!id) return false;
  return row.submittedBy === `apikey:${id}`;
}

function toPublic(row: typeof jobs.$inferSelect) {
  return {
    id: row.id,
    status: row.status,
    jobType: row.jobType,
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
    currentStage: row.currentStage,
    progressPercent: row.progressPercent,
    lastMessage: row.lastMessage,
    resultUrl: row.resultUrl,
    versionId: row.versionId,
    rootObjectId: row.rootObjectId,
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
