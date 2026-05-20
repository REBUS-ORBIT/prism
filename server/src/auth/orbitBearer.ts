/**
 * ORBIT bearer auth: forwards `Authorization: Bearer <orbit-token>` to
 * orbit-server's GraphQL endpoint, asks for `{ activeUser { id } }`,
 * and caches positive results for 5 minutes.
 */
import { request } from 'undici';
import type { FastifyRequest } from 'fastify';
import { getSetting } from '../db/settings.js';

interface CacheEntry {
  userId: string;
  serverUrl: string;
  expiresAt: number;
}
const POSITIVE_TTL_MS = 5 * 60 * 1000;
const cache = new Map<string, CacheEntry>();

const GQL_ACTIVE_USER = `query { activeUser { id } }`;

async function resolveOrbitServerUrl(target: 'prod' | 'dev'): Promise<string | undefined> {
  const key = target === 'dev' ? 'orbit_dev_server_url' : 'orbit_server_url';
  return getSetting(key);
}

/**
 * @returns the ORBIT user id if the token validates, or null if not.
 */
async function validate(token: string, target: 'prod' | 'dev'): Promise<{ userId: string; serverUrl: string } | null> {
  const cached = cache.get(token);
  const now = Date.now();
  if (cached && cached.expiresAt > now) {
    return { userId: cached.userId, serverUrl: cached.serverUrl };
  }

  const serverUrl = await resolveOrbitServerUrl(target);
  if (!serverUrl) return null;

  try {
    const res = await request(`${serverUrl.replace(/\/$/, '')}/graphql`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        authorization: `Bearer ${token}`,
      },
      body: JSON.stringify({ query: GQL_ACTIVE_USER }),
    });
    if (res.statusCode !== 200) return null;
    const body = await res.body.json() as { data?: { activeUser?: { id?: string } } };
    const id = body?.data?.activeUser?.id;
    if (!id) return null;
    cache.set(token, { userId: id, serverUrl, expiresAt: now + POSITIVE_TTL_MS });
    return { userId: id, serverUrl };
  } catch {
    return null;
  }
}

/** Periodically drop expired entries so the cache doesn't grow unbounded. */
setInterval(() => {
  const now = Date.now();
  for (const [k, v] of cache) if (v.expiresAt <= now) cache.delete(k);
}, 60_000).unref();

export async function tryAuthOrbitBearer(req: FastifyRequest, target: 'prod' | 'dev' = 'prod'): Promise<boolean> {
  const auth = req.headers.authorization;
  if (typeof auth !== 'string' || !auth.startsWith('Bearer ')) return false;
  const token = auth.slice(7).trim();
  if (!token) return false;

  const result = await validate(token, target);
  if (!result) return false;

  req.principal = {
    kind: 'orbitUser',
    userId: result.userId,
    orbitToken: token,
    serverUrl: result.serverUrl,
  };
  return true;
}
