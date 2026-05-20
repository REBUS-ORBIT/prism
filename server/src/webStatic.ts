/**
 * Static file serving for the admin + convert SPAs.
 *
 * Vite builds `web/dist/` with this layout:
 *   dist/src/admin/index.html
 *   dist/src/convert/index.html
 *   dist/assets/*.{js,css}
 *
 * Container layout (set by server/Dockerfile):
 *   /prism/web-dist/  (top-level — i.e. dist/)
 *
 * We mount /assets at dist/assets/ and serve hash-routed SPA entry HTML
 * at /admin and /convert. Each SPA uses createWebHashHistory so all
 * client-side routing is fragment-based and no SPA-fallback is needed.
 */
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import type { FastifyInstance } from 'fastify';
import fastifyStatic from '@fastify/static';

export async function registerWebStatic(app: FastifyInstance): Promise<void> {
  const root = resolve(process.env.WEB_DIST_DIR ?? './web-dist');
  if (!existsSync(root)) {
    app.log.warn({ root }, 'web-dist not found; admin + convert SPAs will 404');
    return;
  }

  // Asset bundles (JS / CSS / fonts / images)
  await app.register(fastifyStatic, {
    root: resolve(root, 'assets'),
    prefix: '/assets/',
    decorateReply: false,
  });

  // Admin SPA
  const adminHtmlDir = resolve(root, 'src', 'admin');
  if (existsSync(resolve(adminHtmlDir, 'index.html'))) {
    await app.register(fastifyStatic, {
      root: adminHtmlDir,
      prefix: '/admin/',
      decorateReply: false,
      index: 'index.html',
    });
    app.get('/admin', (_req, reply) => reply.redirect('/admin/'));
  }

  // Convert SPA
  const convertHtmlDir = resolve(root, 'src', 'convert');
  if (existsSync(resolve(convertHtmlDir, 'index.html'))) {
    await app.register(fastifyStatic, {
      root: convertHtmlDir,
      prefix: '/convert/',
      decorateReply: false,
      index: 'index.html',
    });
    app.get('/convert', (_req, reply) => reply.redirect('/convert/'));
  }

  // Root -> admin
  app.get('/', (_req, reply) => reply.redirect('/admin/'));
}
