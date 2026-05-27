/**
 * Typed settings accessor. The `settings` table is a free-form key/value
 * store, but every key PRISM reads goes through one of these helpers so
 * we have a single place to add validation, defaults, and types.
 */
import { eq } from 'drizzle-orm';
import { db } from './client.js';
import { settings } from './schema.js';

const ENV_FALLBACKS: Partial<Record<SettingKey | LegacySettingKey, string | undefined>> = {
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
  | 'maintenance_mode'
  // Optional override for the WSS endpoint baked into the per-node agent
  // config template. Falls back to wss://<request host>/ws/agent.
  | 'workstation_agent_ws_url'
  // Persisted Vue Flow node positions for the admin Pipeline page. Stored
  // as a JSON string of shape:
  //   { "<pipelineId>": { "<nodeId>": { "x": number, "y": number } } }
  // Missing pipelines / nodes fall back to the auto-layout in
  // FlowEditor.vue. Cleared per-pipeline when the user clicks "Reset
  // layout"; cleared globally if the value fails to parse.
  | 'pipeline_layout_v1';

/**
 * Legacy keys that are still read from the DB as a fallback by older code
 * paths (notably `api/workstationDownloads.ts`) but are no longer surfaced
 * in the admin UI as editable. The agent download URL + version are now
 * auto-resolved live from the GitHub Releases API on every request. These
 * union members exist purely to keep existing call sites type-checking
 * until they are removed; do NOT add new keys here.
 */
export type LegacySettingKey =
  | 'workstation_agent_download_url'
  | 'workstation_agent_version';

export async function getSetting(key: SettingKey | LegacySettingKey): Promise<string | undefined> {
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
