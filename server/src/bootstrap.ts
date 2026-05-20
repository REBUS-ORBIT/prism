/**
 * First-boot bootstrap.
 *
 * Runs once per process start (idempotent):
 *   - applies pending migrations
 *   - if there are no admin users, seeds one from ADMIN_USERNAME / ADMIN_PASSWORD
 *   - if `settings.session_secret` is missing, copies from SESSION_SECRET env
 */
import { count } from 'drizzle-orm';
import { migrate } from 'drizzle-orm/node-postgres/migrator';
import { db } from './db/client.js';
import { adminUsers } from './db/schema.js';
import { hashPassword } from './auth/adminSession.js';
import { getSetting, setSetting } from './db/settings.js';
import type { FastifyBaseLogger } from 'fastify';

export async function runBootstrap(log: FastifyBaseLogger): Promise<void> {
  const migrationsFolder = process.env.MIGRATIONS_DIR ?? './src/db/migrations';
  log.info({ migrationsFolder }, 'bootstrap: applying pending migrations');
  await migrate(db, { migrationsFolder });

  const countRows = await db.select({ value: count() }).from(adminUsers);
  const adminCount = countRows[0]?.value ?? 0;
  if (adminCount === 0) {
    const username = process.env.ADMIN_USERNAME ?? 'admin';
    const password = process.env.ADMIN_PASSWORD;
    if (!password) {
      log.warn('bootstrap: no ADMIN_PASSWORD set and admin_users is empty — admin SPA login disabled until you seed manually');
    } else {
      const passwordHash = await hashPassword(password);
      await db.insert(adminUsers).values({ username, passwordHash });
      log.info({ username }, 'bootstrap: seeded initial admin user');
    }
  }

  if (!(await getSetting('orbit_server_url')) && process.env.ORBIT_SERVER_URL) {
    await setSetting('orbit_server_url', process.env.ORBIT_SERVER_URL);
    log.info('bootstrap: seeded orbit_server_url from env');
  }
  if (!(await getSetting('orbit_dev_server_url')) && process.env.ORBIT_DEV_SERVER_URL) {
    await setSetting('orbit_dev_server_url', process.env.ORBIT_DEV_SERVER_URL);
  }
}
