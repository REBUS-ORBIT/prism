/**
 * /api/admin — login / logout / me.
 *
 * Used by the admin SPA (Phase 4). The first admin user is seeded from
 * ADMIN_USERNAME / ADMIN_PASSWORD env vars during the bootstrap step.
 */
import type { FastifyPluginAsync } from 'fastify';
import { z } from 'zod';
import { hashPassword, loginAdmin, logoutAdmin, tryAuthAdminSession } from '../auth/adminSession.js';
import { db } from '../db/client.js';
import { adminUsers } from '../db/schema.js';
import { eq } from 'drizzle-orm';
import { requireAdmin } from '../auth/middleware.js';

const loginBody = z.object({
  username: z.string().min(1).max(64),
  password: z.string().min(1),
});

const changePasswordBody = z.object({
  currentPassword: z.string().min(1),
  newPassword: z.string().min(8),
});

const plugin: FastifyPluginAsync = async (app) => {
  app.post('/login', async (req, reply) => {
    const parsed = loginBody.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body' });
    const ok = await loginAdmin(req, reply, parsed.data.username, parsed.data.password);
    if (!ok) return reply.code(401).send({ error: 'invalid credentials' });
    return { ok: true, username: parsed.data.username };
  });

  app.post('/logout', async (_req, reply) => {
    logoutAdmin(reply);
    return { ok: true };
  });

  app.get('/me', async (req, reply) => {
    if (!(await tryAuthAdminSession(req))) return reply.code(401).send({ error: 'not authenticated' });
    return { kind: 'adminSession', principal: req.principal };
  });

  app.post('/change-password', { preHandler: requireAdmin }, async (req, reply) => {
    const parsed = changePasswordBody.safeParse(req.body);
    if (!parsed.success) return reply.code(400).send({ error: 'invalid body' });
    const principal = req.principal!;
    if (principal.kind !== 'adminSession') return reply.code(403).send({ error: 'admin only' });
    const row = (await db.select().from(adminUsers).where(eq(adminUsers.id, principal.adminUserId)).limit(1))[0];
    if (!row) return reply.code(404).send({ error: 'not found' });
    const bcrypt = await import('bcryptjs');
    const ok = await bcrypt.compare(parsed.data.currentPassword, row.passwordHash);
    if (!ok) return reply.code(401).send({ error: 'invalid current password' });
    const newHash = await hashPassword(parsed.data.newPassword);
    await db.update(adminUsers).set({ passwordHash: newHash }).where(eq(adminUsers.id, row.id));
    return { ok: true };
  });
};

export default plugin;
