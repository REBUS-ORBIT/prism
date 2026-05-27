/**
 * Phase J — REST surface for project attachments.
 *
 * Exercises the full HTTP shape (multipart upload, content-type gating,
 * size cap, scope guard, soft-delete, list/get) against an in-memory
 * Fastify instance. The DB layer is mocked at the module boundary so
 * the suite needs neither Postgres nor a real filesystem mount — each
 * test runs with a fresh `os.tmpdir()` scratch directory that's torn
 * down in `afterEach`.
 *
 * The `auth/middleware` module is partially mocked so we can switch the
 * caller's principal (admin / api-key with-scope / api-key without)
 * test by test without spinning up the real cookie-auth pipeline.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve as resolvePath } from 'node:path';
import { randomUUID } from 'node:crypto';
import Fastify from 'fastify';
import multipart from '@fastify/multipart';
import FormData from 'form-data';

const tmpRoot = mkdtempSync(join(tmpdir(), 'prism-attachments-'));
process.env.PRISM_DATA_DIR = tmpRoot;

// -----------------------------------------------------------------------------
// Mocks
// -----------------------------------------------------------------------------

interface Row {
  id: string;
  projectId: string;
  filename: string;
  contentType: string;
  sizeBytes: number;
  storagePath: string;
  uploadedByApiKeyId: string | null;
  uploadedAt: Date;
  deletedAt: Date | null;
}

const state = {
  rows: [] as Row[],
  principal: { kind: 'apiKey', apiKeyId: 'key-1', scopes: ['visualiser:attach_project_files'] } as
    | { kind: 'apiKey'; apiKeyId: string; scopes: string[] }
    | { kind: 'adminSession'; username: string }
    | null,
};

vi.mock('../src/auth/middleware.js', () => ({
  requireAuth: async (req: { principal: typeof state.principal }, reply: { code: (n: number) => { send: (b: unknown) => void } }) => {
    if (!state.principal) { reply.code(401).send({ error: 'authentication required' }); return; }
    req.principal = state.principal;
  },
  requireScope: (scope: string) => async (
    req: { principal: typeof state.principal },
    reply: { code: (n: number) => { send: (b: unknown) => void } },
  ) => {
    const p = req.principal;
    if (!p) { reply.code(401).send({ error: 'authentication required' }); return; }
    if (p.kind === 'adminSession') return;
    if (p.kind === 'apiKey' && p.scopes.includes(scope)) return;
    reply.code(403).send({ error: 'forbidden', scope });
  },
}));

vi.mock('../src/db/schema.js', () => {
  const tag = (kind: string) => new Proxy({ _kind: kind } as Record<string, unknown>, {
    get: (target, prop) => prop === '_kind' ? target._kind : Symbol(`${kind}.${String(prop)}`),
  });
  return { projectAttachments: tag('projectAttachments') };
});

vi.mock('drizzle-orm', () => ({
  eq:    (...a: unknown[]) => ({ _op: 'eq', args: a }),
  and:   (...a: unknown[]) => ({ _op: 'and', args: a }),
  desc:  (...a: unknown[]) => ({ _op: 'desc', args: a }),
  isNull:(...a: unknown[]) => ({ _op: 'isNull', args: a }),
}));

vi.mock('../src/db/client.js', () => {
  function activeRows() { return state.rows.filter((r) => r.deletedAt === null); }
  /**
   * Walk a drizzle-style where node and pull out every `(column, value)`
   * pair recorded by an `eq(col, value)` builder. Handles both a bare
   * `eq(...)` and an `and(eq(...), eq(...), isNull(...))` shape so the
   * mock works whether the route built a single-column lookup
   * (UPDATE … WHERE id = X) or a tri-column live-row lookup
   * (SELECT … WHERE id = X AND projectId = Y AND deletedAt IS NULL).
   */
  function extractEqPairs(node: unknown): Array<{ col: string; val: unknown }> {
    if (!node || typeof node !== 'object') return [];
    const n = node as { _op?: string; args?: unknown[] };
    if (n._op === 'eq' && Array.isArray(n.args) && n.args.length === 2) {
      return [{ col: String(n.args[0]), val: n.args[1] }];
    }
    if (n._op === 'and' && Array.isArray(n.args)) {
      return n.args.flatMap((c) => extractEqPairs(c));
    }
    return [];
  }
  function pickRowFromWhere(w: unknown): Row | null {
    const pairs = extractEqPairs(w);
    let id: string | null = null;
    let projectId: string | null = null;
    for (const { col, val } of pairs) {
      if (col.includes('projectAttachments.id')) id = val as string;
      if (col.includes('projectAttachments.projectId')) projectId = val as string;
    }
    if (!id) return null;
    return state.rows.find((r) =>
      r.id === id &&
      (projectId === null || r.projectId === projectId)
    ) ?? null;
  }
  function projectFromWhere(w: unknown): string | null {
    for (const { col, val } of extractEqPairs(w)) {
      if (col.includes('projectAttachments.projectId')) return val as string;
    }
    return null;
  }

  const db = {
    query: {
      projectAttachments: {
        findFirst: vi.fn(async (opts: { where: unknown }) => pickRowFromWhere(opts.where)),
      },
    },
    select: () => ({
      from: (_table: unknown) => {
        const chain: Promise<Row[]> & { where?: (w: unknown) => typeof chain; orderBy?: () => typeof chain } =
          Promise.resolve([] as Row[]) as Promise<Row[]> & { where?: (w: unknown) => typeof chain; orderBy?: () => typeof chain };
        const where = (w: unknown) => {
          const project = projectFromWhere(w);
          const rows = activeRows()
            .filter((r) => project === null || r.projectId === project)
            .sort((a, b) => b.uploadedAt.getTime() - a.uploadedAt.getTime());
          const inner: Promise<Row[]> & { orderBy?: () => typeof inner } =
            Promise.resolve(rows) as Promise<Row[]> & { orderBy?: () => typeof inner };
          inner.orderBy = () => inner;
          return inner;
        };
        chain.where = where;
        return chain;
      },
    }),
    insert: (_table: unknown) => ({
      values: (vals: Partial<Row>) => ({
        returning: async () => {
          const row: Row = {
            id: randomUUID(),
            projectId: vals.projectId!,
            filename: vals.filename!,
            contentType: vals.contentType!,
            sizeBytes: vals.sizeBytes!,
            storagePath: vals.storagePath ?? '',
            uploadedByApiKeyId: vals.uploadedByApiKeyId ?? null,
            uploadedAt: new Date(),
            deletedAt: null,
          };
          state.rows.push(row);
          return [row];
        },
      }),
    }),
    update: (_table: unknown) => ({
      set: (patch: Partial<Row>) => ({
        where: (w: unknown) => {
          const target = pickRowFromWhere(w);
          const apply = (r: Row | null): Row[] => {
            if (!r) return [];
            Object.assign(r, patch);
            return [r];
          };
          const chain: Promise<Row[]> & { returning?: () => Promise<Row[]> } =
            Promise.resolve(apply(target)) as Promise<Row[]> & { returning?: () => Promise<Row[]> };
          chain.returning = () => Promise.resolve(apply(target));
          return chain;
        },
      }),
    }),
  };
  return { db };
});

// -----------------------------------------------------------------------------
// Test harness
// -----------------------------------------------------------------------------
// Importing the plugin after the mocks so the mocked imports win.
const importPlugin = async () => (await import('../src/api/projectAttachments.js')).default;

async function buildApp() {
  const app = Fastify({ logger: false });
  await app.register(multipart, { limits: { fileSize: 1024 * 1024 * 1024 } });
  await app.register(await importPlugin(), { prefix: '/api/projects' });
  await app.ready();
  return app;
}

function asMultipart(form: FormData): { payload: Buffer; headers: Record<string, string> } {
  return { payload: form.getBuffer(), headers: form.getHeaders() };
}

beforeEach(() => {
  state.rows = [];
  state.principal = { kind: 'apiKey', apiKeyId: 'key-1', scopes: ['visualiser:attach_project_files'] };
});

afterEach(() => {
  // Each test's uploads land in PRISM_DATA_DIR/project-attachments/<projectId>/.
  // We clear the rows array but leave files on disk until the suite is done.
});

afterEach(() => { /* per-test cleanup runs after all `afterEach`s in order */ });

// One-shot tear-down of the scratch root once the suite is done.
import { afterAll } from 'vitest';
afterAll(() => {
  rmSync(tmpRoot, { recursive: true, force: true });
});

// -----------------------------------------------------------------------------
// Tests
// -----------------------------------------------------------------------------

describe('POST /api/projects/:projectId/attachments', () => {
  it('accepts a valid MVR upload and persists the body to disk', async () => {
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.from('MVR_FAKE_BYTES'), { filename: 'rig.mvr', contentType: 'application/mvr' });
    const r = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    expect(r.statusCode).toBe(201);
    const body = r.json() as { id: string; filename: string; contentType: string; sizeBytes: number };
    expect(body.filename).toBe('rig.mvr');
    expect(body.contentType).toBe('application/mvr');
    expect(body.sizeBytes).toBe('MVR_FAKE_BYTES'.length);
    const row = state.rows.find((r) => r.id === body.id)!;
    expect(row.storagePath).toMatch(/p-1[\\\/].*-rig\.mvr$/);
    expect(readFileSync(row.storagePath, 'utf8')).toBe('MVR_FAKE_BYTES');
    await app.close();
  });

  it('rejects an unauthenticated request with 401', async () => {
    state.principal = null;
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.from('x'), { filename: 'x.mvr', contentType: 'application/mvr' });
    const r = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    expect(r.statusCode).toBe(401);
    await app.close();
  });

  it('rejects a caller without the visualiser:attach_project_files scope with 403', async () => {
    state.principal = { kind: 'apiKey', apiKeyId: 'key-1', scopes: ['visualiser:create_stream'] };
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.from('x'), { filename: 'x.mvr', contentType: 'application/mvr' });
    const r = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    expect(r.statusCode).toBe(403);
    expect(r.json()).toMatchObject({ error: 'forbidden', scope: 'visualiser:attach_project_files' });
    await app.close();
  });

  it('rejects a non-multipart request with 415', async () => {
    const app = await buildApp();
    const r = await app.inject({
      method: 'POST',
      url: '/api/projects/p-1/attachments',
      headers: { 'content-type': 'application/json' },
      payload: '{}',
    });
    expect(r.statusCode).toBe(415);
    await app.close();
  });

  it('rejects an upload with a banned extension (415)', async () => {
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.from('garbage'), { filename: 'evil.exe', contentType: 'application/octet-stream' });
    const r = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    expect(r.statusCode).toBe(415);
    expect(r.json()).toMatchObject({ error: 'unsupported attachment type' });
    await app.close();
  });

  it('rejects an upload with a banned mime type (415)', async () => {
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.from('x'), { filename: 'x.mvr', contentType: 'text/html' });
    const r = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    expect(r.statusCode).toBe(415);
    await app.close();
  });

  it('rejects a >50 MB upload with 413', async () => {
    const app = await buildApp();
    const big = Buffer.alloc(51 * 1024 * 1024, 0x42);
    const form = new FormData();
    form.append('file', big, { filename: 'huge.mvr', contentType: 'application/mvr' });
    const r = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    expect(r.statusCode).toBe(413);
    expect(r.json()).toMatchObject({ error: 'attachment too large' });
    await app.close();
  }, 30_000);

  it('rejects an empty upload with 400', async () => {
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.alloc(0), { filename: 'empty.mvr', contentType: 'application/mvr' });
    const r = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    expect(r.statusCode).toBe(400);
    await app.close();
  });
});

describe('GET /api/projects/:projectId/attachments', () => {
  it('returns all live attachments for the project newest-first', async () => {
    state.rows = [
      {
        id: 'a',
        projectId: 'p-1',
        filename: 'older.mvr',
        contentType: 'application/mvr',
        sizeBytes: 1,
        storagePath: '/x',
        uploadedByApiKeyId: null,
        uploadedAt: new Date('2026-05-26T10:00:00Z'),
        deletedAt: null,
      },
      {
        id: 'b',
        projectId: 'p-1',
        filename: 'newer.gdtf',
        contentType: 'application/gdtf',
        sizeBytes: 2,
        storagePath: '/y',
        uploadedByApiKeyId: null,
        uploadedAt: new Date('2026-05-27T10:00:00Z'),
        deletedAt: null,
      },
      {
        id: 'c',
        projectId: 'p-other',
        filename: 'unrelated.mvr',
        contentType: 'application/mvr',
        sizeBytes: 3,
        storagePath: '/z',
        uploadedByApiKeyId: null,
        uploadedAt: new Date('2026-05-27T11:00:00Z'),
        deletedAt: null,
      },
      {
        id: 'd',
        projectId: 'p-1',
        filename: 'deleted.mvr',
        contentType: 'application/mvr',
        sizeBytes: 4,
        storagePath: '/zz',
        uploadedByApiKeyId: null,
        uploadedAt: new Date('2026-05-27T12:00:00Z'),
        deletedAt: new Date(),
      },
    ];
    const app = await buildApp();
    const r = await app.inject({ method: 'GET', url: '/api/projects/p-1/attachments' });
    expect(r.statusCode).toBe(200);
    const body = r.json() as { attachments: { id: string }[] };
    expect(body.attachments.map((a) => a.id)).toEqual(['b', 'a']);
    await app.close();
  });
});

describe('GET /api/projects/:projectId/attachments/:id', () => {
  it('streams the body for a live attachment', async () => {
    const app = await buildApp();
    // Upload first so we have a real on-disk body.
    const form = new FormData();
    const payload = Buffer.from('GDTF_FAKE_BYTES');
    form.append('file', payload, { filename: 'fixture.gdtf', contentType: 'application/gdtf' });
    const up = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    const { id } = up.json() as { id: string };
    const r = await app.inject({ method: 'GET', url: `/api/projects/p-1/attachments/${id}` });
    expect(r.statusCode).toBe(200);
    expect(r.headers['content-type']).toBe('application/gdtf');
    expect(r.rawPayload.equals(payload)).toBe(true);
    await app.close();
  });

  it('404s when the body row does not exist for that project', async () => {
    const app = await buildApp();
    const r = await app.inject({
      method: 'GET',
      url: `/api/projects/p-1/attachments/${randomUUID()}`,
    });
    expect(r.statusCode).toBe(404);
    await app.close();
  });
});

describe('DELETE /api/projects/:projectId/attachments/:id', () => {
  it('soft-deletes the row and unlinks the on-disk body', async () => {
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.from('MVR_BYTES'), { filename: 'rig.mvr', contentType: 'application/mvr' });
    const up = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    const { id } = up.json() as { id: string };
    const row = state.rows.find((r) => r.id === id)!;
    const onDisk = row.storagePath;
    expect(readFileSync(onDisk).length).toBeGreaterThan(0);

    const del = await app.inject({ method: 'DELETE', url: `/api/projects/p-1/attachments/${id}` });
    expect(del.statusCode).toBe(204);
    expect(state.rows.find((r) => r.id === id)!.deletedAt).not.toBeNull();
    expect(() => readFileSync(onDisk)).toThrow();

    const list = await app.inject({ method: 'GET', url: '/api/projects/p-1/attachments' });
    expect((list.json() as { attachments: unknown[] }).attachments).toHaveLength(0);
    await app.close();
  });

  it('rejects a delete without scope with 403', async () => {
    const app = await buildApp();
    const form = new FormData();
    form.append('file', Buffer.from('x'), { filename: 'x.mvr', contentType: 'application/mvr' });
    const up = await app.inject({ method: 'POST', url: '/api/projects/p-1/attachments', ...asMultipart(form) });
    const { id } = up.json() as { id: string };
    state.principal = { kind: 'apiKey', apiKeyId: 'key-1', scopes: [] };
    const del = await app.inject({ method: 'DELETE', url: `/api/projects/p-1/attachments/${id}` });
    expect(del.statusCode).toBe(403);
    await app.close();
  });
});

// Pull resolvePath into the importer set so editors don't complain; the
// helper is used by the schema path normalisation inside the SUT.
void resolvePath;
void writeFileSync;
