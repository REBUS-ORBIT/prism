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
