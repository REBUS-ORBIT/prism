/**
 * Server-side dispatcher for the prism-assimp pre-conversion sidecar.
 *
 * Selected upload extensions are routed through prism-assimp before the
 * job is enqueued.  The sidecar returns an OBJ+MTL+textures.zip bundle
 * which the existing Rhino agent path already knows how to ingest, so
 * "support a new format" reduces to "Assimp can already read it".
 *
 * Behaviour is controlled by the `ASSIMP_SERVICE_URL` env var:
 *   - unset       -> feature disabled; the gate in convert.ts must reject
 *                    these extensions itself.  In `infra/docker-compose.yml`
 *                    we default it to `http://prism-assimp:8088`, so prod
 *                    deploys get the feature on automatically once the
 *                    sidecar container is up.
 *   - set to URL  -> POST /v1/preconvert with the upload, save the
 *                    resulting zip into UPLOAD_DIR, and rewrite the job's
 *                    file metadata to point to it.
 *
 * The original (pre-converted) upload is removed once the zip lands so
 * we don't double-store large source files.  The agent ultimately sees
 * a `.zip` job exactly as if the user had uploaded an OBJ bundle by hand.
 */

import { readFile, unlink, writeFile } from 'node:fs/promises';
import { extname, join, resolve } from 'node:path';
import { randomUUID } from 'node:crypto';

/** Server-side allowlist of extensions routed through prism-assimp. */
export const ASSIMP_EXTS = new Set<string>([
  '.gltf',
  '.glb',
  '.dae',
  '.blend',
  '.x',
  '.usdz',
]);

export interface PreconvertOptions {
  flattenHierarchy?: boolean;
  /** One of `mm`, `cm`, `m`, `inch`, `ft`. Defaults to `m` server-side. */
  targetUnit?: 'mm' | 'cm' | 'm' | 'inch' | 'ft';
}

export interface PreconvertOutcome {
  /** Absolute path of the new (zip) upload that should be saved on the job row. */
  filePath: string;
  /** New display name shown in the admin UI / job listings (`<basename>.zip`). */
  fileName: string;
  /** Byte size of the zip on disk. */
  fileSize: number;
  /** Always `.zip` -- this is what the agent dispatch path receives. */
  format: '.zip';
  /** The extension we were asked to pre-convert (`.glb`, `.dae`, ...). */
  originalFormat: string;
  /** Wall-clock time the sidecar took, ms.  Useful for surfacing in admin. */
  durationMs: number;
}

export interface MaybePreconvertInput {
  filePath: string;
  fileName: string;
  uploadDir: string;
  options?: PreconvertOptions;
}

/** True when the given extension is an Assimp pre-convert candidate. */
export function isAssimpExt(ext: string): boolean {
  return ASSIMP_EXTS.has(ext.toLowerCase());
}

/** True when the feature is enabled (env var is non-empty). */
export function isPreconvertEnabled(): boolean {
  return !!process.env.ASSIMP_SERVICE_URL && process.env.ASSIMP_SERVICE_URL.trim().length > 0;
}

function preconvertBaseUrl(): string | undefined {
  const raw = process.env.ASSIMP_SERVICE_URL;
  if (!raw) return undefined;
  return raw.trim().replace(/\/+$/, '');
}

/**
 * If the upload's extension is in `ASSIMP_EXTS` and the sidecar is
 * configured, run pre-conversion and return the new file metadata.
 * Otherwise return null and the caller keeps the upload as-is.
 *
 * Throws if the extension is in the set but the sidecar is unreachable
 * or fails -- the caller should surface this as a 502 / 5xx and leave
 * the original upload on disk for retry.
 */
export async function maybePreconvert(input: MaybePreconvertInput): Promise<PreconvertOutcome | null> {
  const ext = extname(input.fileName).toLowerCase();
  if (!isAssimpExt(ext)) return null;

  const baseUrl = preconvertBaseUrl();
  if (!baseUrl) {
    throw new Error(
      `Upload extension ${ext} requires prism-assimp pre-conversion but ASSIMP_SERVICE_URL is unset.`,
    );
  }

  const startedAt = Date.now();

  const fileBytes = await readFile(input.filePath);
  const form = new FormData();
  form.append(
    'file',
    new Blob([fileBytes], { type: 'application/octet-stream' }),
    input.fileName,
  );
  if (input.options?.flattenHierarchy !== undefined) {
    form.append('flatten_hierarchy', String(!!input.options.flattenHierarchy));
  }
  if (input.options?.targetUnit) {
    form.append('target_unit', input.options.targetUnit);
  }
  form.append('return_mode', 'stream');

  const url = `${baseUrl}/v1/preconvert`;
  const resp = await fetch(url, {
    method: 'POST',
    body: form,
  });
  if (!resp.ok) {
    const detail = await safeBodyText(resp);
    throw new Error(`prism-assimp ${resp.status} on ${url}: ${detail}`);
  }
  const zipBytes = Buffer.from(await resp.arrayBuffer());

  const originalBase = input.fileName.replace(new RegExp(`${escapeRe(ext)}$`, 'i'), '');
  const newId = randomUUID();
  const newPath = resolve(join(input.uploadDir, `${newId}.zip`));
  await writeFile(newPath, zipBytes);

  // Best-effort cleanup of the original; don't fail the job if it's
  // already gone (multipart upload may have streamed straight into the
  // final path and been moved).
  await unlink(input.filePath).catch(() => undefined);

  return {
    filePath: newPath,
    fileName: `${originalBase}.zip`,
    fileSize: zipBytes.length,
    format: '.zip',
    originalFormat: ext,
    durationMs: Date.now() - startedAt,
  };
}

async function safeBodyText(resp: Response): Promise<string> {
  try {
    return (await resp.text()).slice(0, 1024);
  } catch {
    return resp.statusText || '<no body>';
  }
}

function escapeRe(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
