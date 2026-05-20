/**
 * Postgres connection pool + Drizzle instance.
 *
 * Importers should use the `db` export. Tests can swap by re-binding
 * `db` via a fresh connection.
 */
import { drizzle, type NodePgDatabase } from 'drizzle-orm/node-postgres';
import pg from 'pg';
import * as schema from './schema.js';

const POSTGRES_URL =
  process.env.POSTGRES_URL
  ?? 'postgres://prism:prism@localhost:5432/prism';

export const pool = new pg.Pool({
  connectionString: POSTGRES_URL,
  max: Number(process.env.POSTGRES_POOL_MAX ?? 10),
  idleTimeoutMillis: 30_000,
});

export const db: NodePgDatabase<typeof schema> = drizzle(pool, { schema });

export { schema };
