/**
 * Apply pending Drizzle migrations.
 *
 * Run from the host during dev with: npm run db:migrate
 * Run from the container at boot via the start script.
 */
import 'dotenv/config';
import { migrate } from 'drizzle-orm/node-postgres/migrator';
import { db, pool } from './client.js';

async function main() {
  console.log('[migrate] applying pending migrations...');
  await migrate(db, { migrationsFolder: './src/db/migrations' });
  console.log('[migrate] done');
  await pool.end();
}

main().catch((err) => {
  console.error('[migrate] failed:', err);
  process.exit(1);
});
