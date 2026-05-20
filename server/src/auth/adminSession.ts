/**
 * Admin session auth.
 *
 * On successful POST /api/admin/login we set an httponly cookie
 * (`prism_admin`) signed by SESSION_SECRET. Subsequent requests carrying
 * that cookie are authenticated as the admin user.
 */
import bcrypt from 'bcryptjs';
import { eq } from 'drizzle-orm';
import type { FastifyReply, FastifyRequest } from 'fastify';
import { db } from '../db/client.js';
import { adminUsers } from '../db/schema.js';

const COOKIE_NAME = 'prism_admin';
const SESSION_TTL_DAYS = 7;

interface SessionPayload {
  uid: string;
  username: string;
  iat: number;
}

export async function loginAdmin(req: FastifyRequest, reply: FastifyReply, username: string, password: string): Promise<boolean> {
  const rows = await db.select().from(adminUsers).where(eq(adminUsers.username, username)).limit(1);
  const row = rows[0];
  if (!row || !row.isActive) return false;
  const ok = await bcrypt.compare(password, row.passwordHash);
  if (!ok) return false;

  await db.update(adminUsers).set({ lastLoginAt: new Date() }).where(eq(adminUsers.id, row.id));

  const payload: SessionPayload = { uid: row.id, username: row.username, iat: Date.now() };
  reply.setCookie(COOKIE_NAME, JSON.stringify(payload), {
    httpOnly: true,
    secure: process.env.NODE_ENV === 'production',
    sameSite: 'lax',
    path: '/',
    signed: true,
    maxAge: SESSION_TTL_DAYS * 24 * 60 * 60,
  });
  req.principal = { kind: 'adminSession', adminUserId: row.id, username: row.username };
  return true;
}

export function logoutAdmin(reply: FastifyReply): void {
  reply.clearCookie(COOKIE_NAME, { path: '/' });
}

export async function tryAuthAdminSession(req: FastifyRequest): Promise<boolean> {
  const raw = req.cookies?.[COOKIE_NAME];
  if (!raw) return false;
  const unsigned = req.unsignCookie(raw);
  if (!unsigned.valid || !unsigned.value) return false;

  let payload: SessionPayload;
  try {
    payload = JSON.parse(unsigned.value);
  } catch {
    return false;
  }

  const ageMs = Date.now() - payload.iat;
  if (ageMs < 0 || ageMs > SESSION_TTL_DAYS * 24 * 60 * 60 * 1000) return false;

  req.principal = { kind: 'adminSession', adminUserId: payload.uid, username: payload.username };
  return true;
}

/** Hash a plaintext admin password for storage. */
export async function hashPassword(plaintext: string): Promise<string> {
  return bcrypt.hash(plaintext, 12);
}
