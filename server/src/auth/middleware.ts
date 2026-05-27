/**
 * Composed auth guards for Fastify route preHandlers.
 *
 * Each guard short-circuits with 503 in maintenance mode (admin sessions
 * still go through so an admin can flip the flag back off — that branch
 * is in `requireAdmin` below).
 */
import type { FastifyReply, FastifyRequest } from 'fastify';
import { isMaintenanceMode } from '../db/settings.js';
import { tryAuthAdminSession } from './adminSession.js';
import { tryAuthApiKey } from './apiKey.js';
import { tryAuthOrbitBearer } from './orbitBearer.js';

async function maintenanceGate(reply: FastifyReply): Promise<boolean> {
  if (await isMaintenanceMode()) {
    reply.code(503).send({ error: 'maintenance mode' });
    return false;
  }
  return true;
}

/**
 * Accept admin session, ORBIT bearer, OR API key — whichever proves first.
 * Used for `/api/*` routes that any authenticated caller may hit.
 */
export async function requireAuth(req: FastifyRequest, reply: FastifyReply): Promise<void> {
  if (!(await maintenanceGate(reply))) return;
  if (await tryAuthAdminSession(req)) return;
  if (await tryAuthOrbitBearer(req)) return;
  if (await tryAuthApiKey(req)) return;
  reply.code(401).send({ error: 'authentication required' });
}

/** Strictly admin session. Bypasses maintenance flag so the admin can disable it. */
export async function requireAdmin(req: FastifyRequest, reply: FastifyReply): Promise<void> {
  if (await tryAuthAdminSession(req)) return;
  reply.code(401).send({ error: 'admin session required' });
}

/** Strictly API key. Public `/v1/*` endpoints use this. */
export async function requireApiKey(req: FastifyRequest, reply: FastifyReply): Promise<void> {
  if (!(await maintenanceGate(reply))) return;
  if (await tryAuthApiKey(req)) return;
  reply.code(401).send({ error: 'api key required' });
}

/** ORBIT bearer required (e.g. for SPA convert flow that already holds an ORBIT token). */
export async function requireOrbitBearer(req: FastifyRequest, reply: FastifyReply): Promise<void> {
  if (!(await maintenanceGate(reply))) return;
  if (await tryAuthOrbitBearer(req)) return;
  reply.code(401).send({ error: 'orbit bearer required' });
}

/**
 * Scope guard for API-key callers — chain after `requireApiKey` (or
 * `requireAuth` when both API keys and admin/ORBIT bearers should pass).
 *
 *   app.post('/streams', { preHandler: [requireApiKey, requireScope('visualiser:create_stream')] }, ...)
 *
 * Behaviour, in order:
 *   1. Admin sessions and ORBIT bearer principals are *not* scope-checked
 *      (they aren't issued with a `scopes[]` and historically have full
 *      access to everything `/api/*` exposes). They pass through.
 *   2. API-key principals with the scope in their `scopes[]` pass.
 *   3. Otherwise 403 with `{ error: 'forbidden', scope: '<scope>' }`.
 *
 * NOTE on backwards compatibility — pre-Phase-A API keys have an empty
 * `scopes[]` (the column default). Those keys explicitly do NOT inherit
 * new scopes; an admin must edit the row to grant access. This is the
 * "deny by default" stance the plan calls for.
 */
export function requireScope(scope: string) {
  return async function scopeGuard(req: FastifyRequest, reply: FastifyReply): Promise<void> {
    const principal = req.principal;
    if (!principal) {
      reply.code(401).send({ error: 'authentication required' });
      return;
    }
    if (principal.kind === 'adminSession' || principal.kind === 'orbitUser') return;
    if (principal.kind === 'apiKey' && principal.scopes.includes(scope)) return;
    reply.code(403).send({ error: 'forbidden', scope });
  };
}
