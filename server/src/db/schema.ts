/**
 * PRISM database schema (Drizzle).
 *
 * Single source of truth — `npm run db:generate` diffs this file against
 * the current migration history to produce a new SQL migration in
 * src/db/migrations/. Never hand-edit DDL.
 */
import {
  pgTable, text, varchar, integer, bigint, boolean, timestamp, uuid, jsonb, index, primaryKey, real,
} from 'drizzle-orm/pg-core';
import { sql } from 'drizzle-orm';

// ---------------------------------------------------------------------------
// Jobs
// ---------------------------------------------------------------------------

export const jobs = pgTable('jobs', {
  id:              uuid('id').primaryKey().defaultRandom(),
  status:          varchar('status', { length: 16 }).notNull().default('queued'),
  // queued | dispatched | processing | complete | failed | cancelled
  format:          varchar('format', { length: 16 }).notNull(),   // .3dm, .dwg, .obj, ...
  fileName:        varchar('file_name', { length: 512 }).notNull(),
  fileSize:        bigint('file_size', { mode: 'number' }).notNull(),
  filePath:        text('file_path').notNull(),                   // server-side staging path
  // ORBIT target
  orbitTarget:     varchar('orbit_target', { length: 8 }).notNull().default('prod'), // prod | dev
  projectId:       varchar('project_id', { length: 32 }).notNull(),
  modelId:         varchar('model_id',   { length: 32 }).notNull(),
  modelName:       varchar('model_name', { length: 256 }),
  // Auth principal that submitted (apiKey id, admin user, or 'orbit-bearer')
  submittedBy:     varchar('submitted_by', { length: 128 }),
  // Conversion options (snapshot at submit time)
  options:         jsonb('options').notNull().default(sql`'{}'::jsonb`),
  // Dispatch
  nodeName:        varchar('node_name', { length: 128 }),
  agentSessionId:  uuid('agent_session_id'),
  // Progress
  currentStage:    varchar('current_stage', { length: 64 }),
  progressPercent: real('progress_percent'),
  lastMessage:     text('last_message'),
  // Outcome
  resultUrl:       text('result_url'),       // full URL on orbit-server
  rootObjectId:    varchar('root_object_id', { length: 64 }),
  versionId:       varchar('version_id', { length: 64 }),
  error:           text('error'),
  // Optional callback
  callbackUrl:     text('callback_url'),
  // Timestamps
  createdAt:       timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
  updatedAt:       timestamp('updated_at', { withTimezone: true }).notNull().defaultNow(),
  completedAt:     timestamp('completed_at', { withTimezone: true }),
}, (t) => ({
  byStatus:    index('jobs_status_idx').on(t.status),
  byCreatedAt: index('jobs_created_at_idx').on(t.createdAt),
  byProject:   index('jobs_project_idx').on(t.projectId),
}));

// Streaming log lines per job. WS broadcasts and SSE responses select from here.
export const jobLogs = pgTable('job_logs', {
  id:    bigint('id', { mode: 'number' }).primaryKey().generatedAlwaysAsIdentity(),
  jobId: uuid('job_id').notNull().references(() => jobs.id, { onDelete: 'cascade' }),
  ts:    timestamp('ts', { withTimezone: true }).notNull().defaultNow(),
  level: varchar('level', { length: 8 }).notNull(),
  source: varchar('source', { length: 16 }).notNull(),  // 'server' | 'agent'
  message: text('message').notNull(),
}, (t) => ({
  byJob: index('job_logs_job_idx').on(t.jobId, t.ts),
}));

// ---------------------------------------------------------------------------
// API keys — for external /v1/* callers (X-API-Key header)
// ---------------------------------------------------------------------------

export const apiKeys = pgTable('api_keys', {
  id:         uuid('id').primaryKey().defaultRandom(),
  name:       varchar('name', { length: 128 }).notNull(),
  // SHA-256 hex of the plaintext key. Plaintext is shown to the user
  // once at create time and never persisted.
  keyHash:    varchar('key_hash', { length: 64 }).notNull().unique(),
  // Rate limit (per minute). Null = unlimited.
  rateLimitPerMin: integer('rate_limit_per_min'),
  // Per-month quota (job count). Null = unlimited.
  monthlyQuota:    integer('monthly_quota'),
  isActive:   boolean('is_active').notNull().default(true),
  createdAt:  timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
  lastUsedAt: timestamp('last_used_at', { withTimezone: true }),
});

// ---------------------------------------------------------------------------
// Settings (key/value)
// ---------------------------------------------------------------------------

export const settings = pgTable('settings', {
  key:   varchar('key', { length: 64 }).primaryKey(),
  value: text('value').notNull(),
  updatedAt: timestamp('updated_at', { withTimezone: true }).notNull().defaultNow(),
});

// Settings keys PRISM expects (consumed via getSetting() in server/src/orbit/client.ts etc.):
//   orbit_server_url, orbit_dev_server_url,    target URLs (admin-editable)
//   orbit_token, orbit_dev_token,              optional shared service tokens
//   job_retention_hours                        how long completed job rows survive
//   maintenance_mode                           '1' = block all auth, return 503
//   session_secret                             cookie signer (initialised from env on first boot)

// ---------------------------------------------------------------------------
// Layer presets — saved per (project_id, model_name)
// ---------------------------------------------------------------------------

export const layerPresets = pgTable('layer_presets', {
  id:        uuid('id').primaryKey().defaultRandom(),
  projectId: varchar('project_id', { length: 32 }).notNull(),
  modelName: varchar('model_name', { length: 256 }).notNull(),
  includedLayers: jsonb('included_layers').notNull().default(sql`'[]'::jsonb`),
  knownLayers:    jsonb('known_layers').notNull().default(sql`'[]'::jsonb`),
  includeDescendants: boolean('include_descendants').notNull().default(true),
  updatedAt: timestamp('updated_at', { withTimezone: true }).notNull().defaultNow(),
}, (t) => ({
  byTarget: index('layer_presets_target_idx').on(t.projectId, t.modelName),
}));

// ---------------------------------------------------------------------------
// Admin users
// ---------------------------------------------------------------------------

export const adminUsers = pgTable('admin_users', {
  id:           uuid('id').primaryKey().defaultRandom(),
  username:     varchar('username', { length: 64 }).notNull().unique(),
  passwordHash: varchar('password_hash', { length: 128 }).notNull(),  // bcrypt
  isActive:     boolean('is_active').notNull().default(true),
  createdAt:    timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
  lastLoginAt:  timestamp('last_login_at', { withTimezone: true }),
});

// ---------------------------------------------------------------------------
// Workstations — persistent agent identities
// ---------------------------------------------------------------------------

export const workstations = pgTable('workstations', {
  id:          uuid('id').primaryKey().defaultRandom(),
  machineId:   varchar('machine_id', { length: 64 }).notNull().unique(),  // stable GUID from the agent
  nodeName:    varchar('node_name', { length: 128 }).notNull(),
  // Capability flags — first-class booleans rather than a CSV string.
  canConvert:  boolean('can_convert').notNull().default(true),
  canLayer:    boolean('can_layer').notNull().default(true),
  canReceive:  boolean('can_receive').notNull().default(false),
  // Reported by the agent on `hello`.
  supportedFormats: jsonb('supported_formats').notNull().default(sql`'[]'::jsonb`),
  slotsTotal:       integer('slots_total').notNull().default(1),
  agentVersion:     varchar('agent_version', { length: 32 }),
  rhinoVersion:     varchar('rhino_version', { length: 32 }),
  isEnabled:        boolean('is_enabled').notNull().default(true),
  notes:            text('notes'),
  createdAt:    timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
  lastSeenAt:   timestamp('last_seen_at', { withTimezone: true }),
});

// Live WS session per agent connection. Insert on `hello`, delete on disconnect.
// Outlives the WS process if there's a clean shutdown miss — the dispatcher
// double-checks the connection before assigning.
export const agentSessions = pgTable('agent_sessions', {
  id:            uuid('id').primaryKey().defaultRandom(),
  workstationId: uuid('workstation_id').notNull().references(() => workstations.id, { onDelete: 'cascade' }),
  connectedAt:   timestamp('connected_at', { withTimezone: true }).notNull().defaultNow(),
  lastHeartbeat: timestamp('last_heartbeat', { withTimezone: true }),
  remoteAddr:    varchar('remote_addr', { length: 64 }),
  slotsBusy:     integer('slots_busy').notNull().default(0),
}, (t) => ({
  byWorkstation: index('agent_sessions_workstation_idx').on(t.workstationId),
}));

// ---------------------------------------------------------------------------
// Webhook endpoints — admin-configured callback targets
// ---------------------------------------------------------------------------

export const webhooks = pgTable('webhooks', {
  id:        uuid('id').primaryKey().defaultRandom(),
  name:      varchar('name', { length: 128 }).notNull(),
  url:       text('url').notNull(),
  secret:    varchar('secret', { length: 64 }),         // HMAC sig secret
  events:    jsonb('events').notNull().default(sql`'["job.complete","job.failed"]'::jsonb`),
  isActive:  boolean('is_active').notNull().default(true),
  createdAt: timestamp('created_at', { withTimezone: true }).notNull().defaultNow(),
});

// ---------------------------------------------------------------------------
// Exported type helpers
// ---------------------------------------------------------------------------

export type Job        = typeof jobs.$inferSelect;
export type NewJob     = typeof jobs.$inferInsert;
export type JobLog     = typeof jobLogs.$inferSelect;
export type ApiKey     = typeof apiKeys.$inferSelect;
export type Setting    = typeof settings.$inferSelect;
export type LayerPreset= typeof layerPresets.$inferSelect;
export type AdminUser  = typeof adminUsers.$inferSelect;
export type Workstation= typeof workstations.$inferSelect;
export type AgentSession = typeof agentSessions.$inferSelect;
export type Webhook    = typeof webhooks.$inferSelect;
