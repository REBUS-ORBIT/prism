import 'dotenv/config';
import type { Config } from 'drizzle-kit';

export default {
  schema: './src/db/schema.ts',
  out:    './src/db/migrations',
  dialect: 'postgresql',
  dbCredentials: {
    url: process.env.POSTGRES_URL ?? 'postgres://prism:prism@localhost:5432/prism',
  },
  strict: true,
  verbose: true,
} satisfies Config;
