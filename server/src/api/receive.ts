/**
 * POST /api/receive/async
 *   { projectId, modelId, versionId, orbitTarget?, outputFormat? = '3dm' }
 *
 * Queues a `receive` job. The dispatcher routes it to a workstation
 * with the `receive` capability; the agent pulls the version from
 * ORBIT, hydrates raw encoding, writes the requested output format
 * (.3dm by default; .step also supported when Rhino has the importer),
 * and uploads the bytes back via /internal/outputs/<jobId>/<format>.
 *
 * Clients then GET /api/jobs/:id/outputs/<format> for the file once
 * `status === 'complete'`.
 */
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import type { FastifyPluginAsync } from 'fastify';
import { z } from 'zod';
import { db } from '../db/client.js';
import { jobs } from '../db/schema.js';
import { convertQueue } from '../jobs/queue.js';
import { requireAuth } from '../auth/middleware.js';

const ALLOWED_OUTPUTS = new Set(['3dm', 'step']);

const body = z.object({
  projectId: z.string().min(1),
  modelId: z.string().min(1),
  versionId: z.string().min(1),
  modelName: z.string().optional(),
  orbitTarget: z.enum(['prod', 'dev']).optional(),
  outputFormat: z.string().optional(),
  callbackUrl: z.string().url().optional(),
});

const plugin: FastifyPluginAsync = async (app) => {
  app.addHook('preHandler', requireAuth);

  app.post('/async', async (req, reply) => {
    const parsed = body.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body', detail: parsed.error.flatten() });
    const b = parsed.data;
    const outputFormat = (b.outputFormat ?? '3dm').toLowerCase();
    if (!ALLOWED_OUTPUTS.has(outputFormat)) {
      return reply.code(400).send({ error: `outputFormat must be one of ${[...ALLOWED_OUTPUTS].join(', ')}` });
    }

    // Each receive job gets a working directory so the agent's uploads land
    // in a deterministic place per job.
    const stageRoot = process.env.UPLOAD_DIR ?? './uploads';
    const stageDir = join(stageRoot, 'outputs');
    await mkdir(stageDir, { recursive: true });

    const principal = (req as { principal?: { kind: string; principal?: { id?: string; username?: string } } }).principal;
    const submittedBy =
      principal?.kind === 'apiKey'        ? `apiKey:${principal?.principal?.id ?? ''}` :
      principal?.kind === 'admin'         ? `admin:${principal?.principal?.username ?? ''}` :
      principal?.kind === 'orbit-bearer'  ? `orbit:${principal?.principal?.id ?? ''}` :
      'unknown';

    const [row] = await db.insert(jobs).values({
      status: 'queued',
      jobType: 'receive',
      format: outputFormat,                                  // for receive the "format" column holds the OUTPUT extension
      fileName: `${b.modelName ?? b.modelId}-${b.versionId.slice(0, 8)}.${outputFormat}`,
      fileSize: 0,
      filePath: stageDir,
      orbitTarget: b.orbitTarget ?? 'prod',
      projectId: b.projectId,
      modelId: b.modelId,
      modelName: b.modelName,
      receiveVersionId: b.versionId,
      outputFormats: [outputFormat],
      submittedBy,
      callbackUrl: b.callbackUrl,
    }).returning();
    if (!row) return reply.code(500).send({ error: 'failed to persist job' });

    await convertQueue.add('receive', { jobId: row.id }, { removeOnComplete: 100, removeOnFail: 100 });

    return reply.code(202).send({ jobId: row.id, status: 'queued' });
  });
};

export default plugin;
