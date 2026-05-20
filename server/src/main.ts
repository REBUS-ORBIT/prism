/**
 * PRISM Server entry point.
 *
 * Phase 1 wiring: Fastify app + cookie + multipart + cors + auth +
 * REST routes for jobs / convert / admin / settings / keys /
 * workstations / layer-presets. The WS gateway lands in Phase 2.
 */
import 'dotenv/config';
import Fastify from 'fastify';
import cookie from '@fastify/cookie';
import cors from '@fastify/cors';
import multipart from '@fastify/multipart';
import { runBootstrap } from './bootstrap.js';

const PORT = Number(process.env.PORT ?? 8765);
const HOST = process.env.HOST ?? '0.0.0.0';
const LOG_LEVEL = process.env.LOG_LEVEL ?? 'info';

async function buildApp() {
  const app = Fastify({
    logger: {
      level: LOG_LEVEL,
      transport: process.env.NODE_ENV === 'production'
        ? undefined
        : { target: 'pino-pretty', options: { translateTime: 'SYS:HH:MM:ss.l', ignore: 'pid,hostname' } },
    },
    bodyLimit: 64 * 1024 * 1024,  // small JSON bodies; uploads go through @fastify/multipart
    disableRequestLogging: false,
  });

  const sessionSecret = process.env.SESSION_SECRET;
  if (!sessionSecret) {
    app.log.warn('SESSION_SECRET is not set — admin login cookies will not be signable. Set this in production!');
  }
  await app.register(cookie, { secret: sessionSecret ?? 'unsafe-dev-only-do-not-use-in-prod' });
  await app.register(cors, {
    origin: (origin, cb) => {
      // Same-origin (no Origin header) is always fine. Cross-origin only in dev.
      if (!origin) return cb(null, true);
      if (process.env.NODE_ENV !== 'production') return cb(null, true);
      const allowed = (process.env.CORS_ALLOWED_ORIGINS ?? '').split(',').map((s) => s.trim()).filter(Boolean);
      cb(null, allowed.includes(origin));
    },
    credentials: true,
  });
  await app.register(multipart, {
    limits: {
      fileSize: 1024 * 1024 * 1024,  // 1 GB
      files: 1,
      fields: 32,
    },
  });

  app.get('/health', async () => ({
    status: 'ok',
    service: 'prism-server',
    version: process.env.npm_package_version ?? '0.1.0',
    phase: 1,
  }));

  await app.register(import('./api/admin.js'),         { prefix: '/api/admin' });
  await app.register(import('./api/jobs.js'),          { prefix: '/api/jobs' });
  await app.register(import('./api/convert.js'),       { prefix: '/api/convert' });
  await app.register(import('./api/settings.js'),      { prefix: '/api/settings' });
  await app.register(import('./api/keys.js'),          { prefix: '/api/keys' });
  await app.register(import('./api/workstations.js'),  { prefix: '/api/workstations' });
  await app.register(import('./api/layerPresets.js'),  { prefix: '/api/layer-presets' });

  return app;
}

async function main() {
  const app = await buildApp();
  try {
    await runBootstrap(app.log);
  } catch (err) {
    app.log.error({ err }, 'bootstrap failed');
    process.exit(1);
  }

  try {
    await app.listen({ host: HOST, port: PORT });
  } catch (err) {
    app.log.error(err);
    process.exit(1);
  }

  for (const sig of ['SIGINT', 'SIGTERM'] as const) {
    process.on(sig, async () => {
      app.log.info({ sig }, 'shutdown');
      await app.close();
      process.exit(0);
    });
  }
}

main();
