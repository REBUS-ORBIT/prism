/**
 * /api/projects/:projectId/attachments — portal-uploaded files attached to
 * an ORBIT project.
 *
 * Phase J — these are PRISM-local files (NOT ORBIT MinIO blobs) that the
 * visualiser orchestrator pulls into `stage/{runId}/attachments/` so the
 * MvrGdtfDetector can wire them into the Unreal world via the DMX plugin.
 *
 * Surface:
 *
 *   POST   /api/projects/:projectId/attachments
 *     Auth: requireAuth + requireScope('visualiser:attach_project_files')
 *     Multipart upload (one file part). 50 MB hard cap (mirrors the
 *     `application/mvr` / `application/gdtf` payloads we expect — a
 *     full Vectorworks lighting export is normally a few MB).
 *     Allowed mime types are gated to the lighting set so a portal user
 *     can't smuggle arbitrary binaries through this endpoint.
 *     Returns { id, filename, contentType, sizeBytes, uploadedAt }.
 *
 *   GET    /api/projects/:projectId/attachments
 *     Auth: requireAuth
 *     Returns { attachments: PublicAttachment[] } in newest-first order.
 *
 *   GET    /api/projects/:projectId/attachments/:id
 *     Auth: requireAuth
 *     Streams the body with the recorded content-type.
 *
 *   DELETE /api/projects/:projectId/attachments/:id
 *     Auth: requireAuth + requireScope('visualiser:attach_project_files')
 *     Soft-deletes: stamps `deleted_at` and unlinks the on-disk body.
 *     Once soft-deleted the row is excluded from the LIST/GET surface
 *     and from the visualiser StartVisualisation envelope.
 *
 * Storage layout (DATA_DIR defaults to `/data/prism`):
 *
 *     ${DATA_DIR}/project-attachments/<projectId>/<id>-<sanitised-filename>
 *
 * The on-disk filename is prefixed with the UUID so per-project collisions
 * are impossible without compromising audit-ability.
 */
import { createReadStream } from 'node:fs';
import { mkdir, stat, unlink, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import type { FastifyPluginAsync } from 'fastify';
import { z } from 'zod';
import { and, desc, eq, isNull } from 'drizzle-orm';
import { db } from '../db/client.js';
import { projectAttachments } from '../db/schema.js';
import { requireAuth, requireScope } from '../auth/middleware.js';

const DATA_DIR = process.env.PRISM_DATA_DIR ?? process.env.DATA_DIR ?? '/data/prism';
const ATTACHMENTS_ROOT = resolve(DATA_DIR, 'project-attachments');

// 50 MB hard cap — full Vectorworks lighting exports + GDTF fixture
// libraries are normally < 5 MB; 50 MB leaves comfortable headroom for
// archives that bundle several fixture defs together.
const MAX_BODY_BYTES = 50 * 1024 * 1024;

// Whitelisted mime types. `application/octet-stream` is included because
// browsers routinely fail to map .mvr / .gdtf to a registered type and
// fall back to octet-stream; we re-validate the extension at the same
// time so the surface stays tight.
const ALLOWED_MIME_TYPES = new Set([
  'application/mvr',
  'application/gdtf',
  'application/zip',
  'application/octet-stream',
]);

const ALLOWED_EXTENSIONS = new Set(['.mvr', '.gdtf', '.zip']);

const projectParam = z.object({ projectId: z.string().min(1).max(128) });
const idParam = z.object({ id: z.string().uuid() });

interface PublicAttachment {
  id: string;
  projectId: string;
  filename: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: string;
  uploadedByApiKeyId: string | null;
}

function toPublic(row: typeof projectAttachments.$inferSelect): PublicAttachment {
  return {
    id: row.id,
    projectId: row.projectId,
    filename: row.filename,
    contentType: row.contentType,
    sizeBytes: row.sizeBytes,
    uploadedAt: row.uploadedAt.toISOString(),
    uploadedByApiKeyId: row.uploadedByApiKeyId,
  };
}

/** Sanitise an upload filename for use on disk. Strips path separators and
 * collapses anything that's not a safe filesystem char. The DB still holds
 * the original `filename` for the LIST surface. */
function sanitiseFilename(input: string): string {
  const base = input.replace(/[\\/]+/g, '_');
  return base.replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 200) || 'attachment';
}

function extOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot === -1 ? '' : name.slice(dot).toLowerCase();
}

const plugin: FastifyPluginAsync = async (app) => {
  await mkdir(ATTACHMENTS_ROOT, { recursive: true }).catch(() => { /* race-tolerant */ });

  /* ---------- POST /api/projects/:projectId/attachments ---------- */
  app.post<{ Params: { projectId: string } }>('/:projectId/attachments', {
    preHandler: [requireAuth, requireScope('visualiser:attach_project_files')],
  }, async (req, reply) => {
    const params = projectParam.safeParse(req.params);
    if (!params.success) return reply.code(400).send({ error: 'invalid projectId' });
    if (!req.isMultipart()) return reply.code(415).send({ error: 'multipart/form-data required' });

    const part = await req.file({ limits: { fileSize: MAX_BODY_BYTES + 1 } });
    if (!part) return reply.code(400).send({ error: 'file part missing' });

    const rawFilename = part.filename || 'attachment';
    const ext = extOf(rawFilename);
    if (ext === '' || !ALLOWED_EXTENSIONS.has(ext)) {
      return reply.code(415).send({
        error: 'unsupported attachment type',
        detail: `extension '${ext || '(none)'}' is not in the allowed set`,
        allowedExtensions: [...ALLOWED_EXTENSIONS],
      });
    }

    const mime = (part.mimetype || '').toLowerCase().split(';')[0]!.trim();
    if (mime && !ALLOWED_MIME_TYPES.has(mime)) {
      return reply.code(415).send({
        error: 'unsupported attachment type',
        detail: `mime '${mime}' is not in the allowed set`,
        allowedMimeTypes: [...ALLOWED_MIME_TYPES],
      });
    }

    // Drain the multipart stream into a buffer so we can enforce the
    // 50 MB cap precisely (fastify-multipart truncates on overflow and
    // signals via the `truncated` flag).
    const chunks: Buffer[] = [];
    let bytesSoFar = 0;
    for await (const chunk of part.file) {
      const buf = chunk as Buffer;
      bytesSoFar += buf.length;
      if (bytesSoFar > MAX_BODY_BYTES || part.file.truncated) {
        return reply.code(413).send({
          error: 'attachment too large',
          maxBytes: MAX_BODY_BYTES,
        });
      }
      chunks.push(buf);
    }
    if (part.file.truncated) {
      return reply.code(413).send({ error: 'attachment too large', maxBytes: MAX_BODY_BYTES });
    }
    if (bytesSoFar === 0) return reply.code(400).send({ error: 'attachment is empty' });

    const body = Buffer.concat(chunks, bytesSoFar);

    // Stable on-disk filename — UUID prefix prevents per-project
    // collisions without sacrificing audit-ability.
    const principal = req.principal!;
    const uploadedByApiKeyId = principal.kind === 'apiKey' ? principal.apiKeyId : null;
    const projectDir = join(ATTACHMENTS_ROOT, params.data.projectId);
    await mkdir(projectDir, { recursive: true });

    const inserted = await db
      .insert(projectAttachments)
      .values({
        projectId: params.data.projectId,
        filename: rawFilename.slice(0, 512),
        contentType: mime || 'application/octet-stream',
        sizeBytes: bytesSoFar,
        // Placeholder; replaced below once we know the row id.
        storagePath: '',
        uploadedByApiKeyId,
      })
      .returning();

    const row = inserted[0]!;
    const storagePath = resolve(projectDir, `${row.id}-${sanitiseFilename(rawFilename)}`);
    await writeFile(storagePath, body);

    const updated = await db
      .update(projectAttachments)
      .set({ storagePath })
      .where(eq(projectAttachments.id, row.id))
      .returning();

    return reply.code(201).send(toPublic(updated[0]!));
  });

  /* ---------- GET /api/projects/:projectId/attachments ---------- */
  app.get<{ Params: { projectId: string } }>('/:projectId/attachments', {
    preHandler: requireAuth,
  }, async (req, reply) => {
    const params = projectParam.safeParse(req.params);
    if (!params.success) return reply.code(400).send({ error: 'invalid projectId' });
    const rows = await db
      .select()
      .from(projectAttachments)
      .where(and(
        eq(projectAttachments.projectId, params.data.projectId),
        isNull(projectAttachments.deletedAt),
      ))
      .orderBy(desc(projectAttachments.uploadedAt));
    return reply.send({ attachments: rows.map(toPublic) });
  });

  /* ---------- GET /api/projects/:projectId/attachments/:id ---------- */
  app.get<{ Params: { projectId: string; id: string } }>('/:projectId/attachments/:id', {
    preHandler: requireAuth,
  }, async (req, reply) => {
    const params = projectParam.safeParse({ projectId: req.params.projectId });
    const idParse = idParam.safeParse({ id: req.params.id });
    if (!params.success || !idParse.success) return reply.code(400).send({ error: 'invalid params' });

    const row = await db.query.projectAttachments.findFirst({
      where: and(
        eq(projectAttachments.id, idParse.data.id),
        eq(projectAttachments.projectId, params.data.projectId),
        isNull(projectAttachments.deletedAt),
      ),
    });
    if (!row) return reply.code(404).send({ error: 'not found' });

    try {
      const s = await stat(row.storagePath);
      reply
        .header('content-type', row.contentType)
        .header('content-length', String(s.size))
        .header('content-disposition', `attachment; filename="${encodeURIComponent(row.filename)}"`);
      return reply.send(createReadStream(row.storagePath));
    } catch {
      return reply.code(410).send({ error: 'attachment body missing on disk' });
    }
  });

  /* ---------- DELETE /api/projects/:projectId/attachments/:id ---------- */
  app.delete<{ Params: { projectId: string; id: string } }>('/:projectId/attachments/:id', {
    preHandler: [requireAuth, requireScope('visualiser:attach_project_files')],
  }, async (req, reply) => {
    const params = projectParam.safeParse({ projectId: req.params.projectId });
    const idParse = idParam.safeParse({ id: req.params.id });
    if (!params.success || !idParse.success) return reply.code(400).send({ error: 'invalid params' });

    const row = await db.query.projectAttachments.findFirst({
      where: and(
        eq(projectAttachments.id, idParse.data.id),
        eq(projectAttachments.projectId, params.data.projectId),
        isNull(projectAttachments.deletedAt),
      ),
    });
    if (!row) return reply.code(404).send({ error: 'not found' });

    await db
      .update(projectAttachments)
      .set({ deletedAt: new Date() })
      .where(eq(projectAttachments.id, row.id));

    try { await unlink(row.storagePath); } catch { /* already gone — fine */ }

    return reply.code(204).send();
  });

  void dirname;
};

export default plugin;
