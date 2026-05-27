/**
 * API key auth: `X-API-Key: <plaintext>` -> SHA-256 -> lookup in `api_keys`.
 */
import { createHash } from 'node:crypto';
import { eq } from 'drizzle-orm';
import type { FastifyReply, FastifyRequest } from 'fastify';
import { db } from '../db/client.js';
import { apiKeys } from '../db/schema.js';

export function hashApiKey(plaintext: string): string {
  return createHash('sha256').update(plaintext, 'utf8').digest('hex');
}

/** Mint a fresh plaintext key. Caller is responsible for showing it to the user once. */
export function mintApiKey(): { plaintext: string; hash: string } {
  // 32 random bytes, base64url, prefixed for visual identification ("prism_").
  const raw = Buffer.from(crypto.getRandomValues(new Uint8Array(32)));
  const plaintext = 'prism_' + raw.toString('base64url');
  return { plaintext, hash: hashApiKey(plaintext) };
}

export async function tryAuthApiKey(req: FastifyRequest): Promise<boolean> {
  const header = req.headers['x-api-key'];
  if (typeof header !== 'string' || header.length === 0) return false;

  const hash = hashApiKey(header);
  const rows = await db.select().from(apiKeys).where(eq(apiKeys.keyHash, hash)).limit(1);
  const row = rows[0];
  if (!row || !row.isActive) return false;

  const scopes = Array.isArray(row.scopes) ? row.scopes.filter((s): s is string => typeof s === 'string') : [];
  req.principal = { kind: 'apiKey', apiKeyId: row.id, apiKeyName: row.name, scopes };

  // Touch last_used_at in the background. Fire-and-forget.
  void db
    .update(apiKeys)
    .set({ lastUsedAt: new Date() })
    .where(eq(apiKeys.id, row.id))
    .catch((err) => req.log.warn({ err }, 'failed to bump api_keys.last_used_at'));

  return true;
}

/** Convenience: enforce API key on a route. */
export async function requireApiKey(req: FastifyRequest, reply: FastifyReply): Promise<void> {
  if (await tryAuthApiKey(req)) return;
  reply.code(401).send({ error: 'api key required' });
}
