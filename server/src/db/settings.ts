/**
 * Typed settings accessor. The `settings` table is a free-form key/value
 * store, but every key PRISM reads goes through one of these helpers so
 * we have a single place to add validation, defaults, and types.
 */
import { eq } from 'drizzle-orm';
import { db } from './client.js';
import { settings } from './schema.js';

const ENV_FALLBACKS: Partial<Record<SettingKey, string | undefined>> = {
  orbit_server_url:     process.env.ORBIT_SERVER_URL,
  orbit_dev_server_url: process.env.ORBIT_DEV_SERVER_URL,
  job_retention_hours:  process.env.JOB_RETENTION_HOURS ?? '720',
  maintenance_mode:     process.env.MAINTENANCE_MODE ?? '0',
};

export type SettingKey =
  | 'orbit_server_url'
  | 'orbit_dev_server_url'
  | 'orbit_token'
  | 'orbit_dev_token'
  | 'job_retention_hours'
  | 'maintenance_mode';

export async function getSetting(key: SettingKey): Promise<string | undefined> {
  const rows = await db.select().from(settings).where(eq(settings.key, key)).limit(1);
  const dbVal = rows[0]?.value;
  if (dbVal !== undefined) return dbVal;
  return ENV_FALLBACKS[key];
}

export async function setSetting(key: SettingKey, value: string): Promise<void> {
  await db
    .insert(settings)
    .values({ key, value })
    .onConflictDoUpdate({ target: settings.key, set: { value, updatedAt: new Date() } });
}

export async function getAllSettings(): Promise<Record<string, string>> {
  const rows = await db.select().from(settings);
  const out: Record<string, string> = {};
  for (const r of rows) out[r.key] = r.value;
  return out;
}

export async function isMaintenanceMode(): Promise<boolean> {
  return (await getSetting('maintenance_mode')) === '1';
}
