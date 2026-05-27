/**
 * Public API documentation surface.
 *
 *  GET /api/openapi.json           -> OpenAPI 3.1 spec for /v1/* and
 *                                     /api/visualiser/* (JSON)
 *  GET /docs                       -> Redoc-rendered HTML (single page)
 *  GET /docs/                      -> same
 *  GET /docs/portal-integration    -> Rendered HTML of
 *                                     docs/PORTAL_INTEGRATION.md (Phase K)
 *  GET /docs/portal-integration.md -> Raw markdown source of same
 *
 * No authentication. The spec describes how to authenticate (X-API-Key) and
 * exposing it openly is fine — third-party developers need to read it to
 * decide whether to integrate at all.
 *
 * Phase K added the portal-integration narrative companion to the
 * machine-readable spec. The markdown source lives in the repo root's
 * `docs/` folder; the production server image copies it to
 * `/prism/docs/PORTAL_INTEGRATION.md`. Set `DOCS_DIR` env to override
 * (defaults: container `/prism/docs`, local dev resolves relative to
 * the working directory).
 */
import { readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import type { FastifyPluginAsync } from 'fastify';
import MarkdownIt from 'markdown-it';
import { buildOpenApi } from './openapi.js';

const md = new MarkdownIt({
  html: false,        // user content is trusted (in-repo), but defence in depth
  linkify: true,
  typographer: true,
  breaks: false,
});

const plugin: FastifyPluginAsync = async (app) => {
  const publicBaseUrl = process.env.PUBLIC_BASE_URL ?? 'https://prism.rebus.industries';
  const docsDir = resolve(process.env.DOCS_DIR ?? (process.env.NODE_ENV === 'production' ? '/prism/docs' : 'docs'));

  // ---- machine-readable spec ----
  app.get('/api/openapi.json', async (_req, reply) => {
    reply
      .header('cache-control', 'public, max-age=300')
      .header('access-control-allow-origin', '*');
    return buildOpenApi(publicBaseUrl);
  });

  // ---- human-readable Redoc page (the OpenAPI spec) ----
  const redocHtml = renderRedocPage(`${publicBaseUrl}/api/openapi.json`);
  for (const path of ['/docs', '/docs/']) {
    app.get(path, async (_req, reply) => {
      reply.header('cache-control', 'public, max-age=60');
      reply.type('text/html; charset=utf-8');
      return redocHtml;
    });
  }

  // ---- portal-integration narrative companion (Phase K) ----
  //
  // Cache the markdown once at process start; the file is shipped
  // inside the docker image so it's immutable across the container's
  // lifetime. A SIGHUP-style reload would be over-engineering — a
  // redeploy ships the updated content.
  let portalMd: string | null = null;
  let portalHtml: string | null = null;
  async function loadPortalDoc(): Promise<{ md: string; html: string }> {
    if (portalMd && portalHtml) return { md: portalMd, html: portalHtml };
    const mdPath = join(docsDir, 'PORTAL_INTEGRATION.md');
    portalMd = await readFile(mdPath, 'utf-8');
    portalHtml = renderPortalDocHtml(portalMd, publicBaseUrl);
    return { md: portalMd, html: portalHtml };
  }

  app.get('/docs/portal-integration', async (_req, reply) => {
    try {
      const { html } = await loadPortalDoc();
      reply
        .header('cache-control', 'public, max-age=300')
        .type('text/html; charset=utf-8');
      return html;
    } catch (err) {
      app.log.error({ err }, 'failed to render portal-integration doc');
      reply.code(404);
      return { error: 'portal integration guide not available' };
    }
  });

  app.get('/docs/portal-integration.md', async (_req, reply) => {
    try {
      const { md } = await loadPortalDoc();
      reply
        .header('cache-control', 'public, max-age=300')
        .header('access-control-allow-origin', '*')
        .type('text/markdown; charset=utf-8');
      return md;
    } catch (err) {
      app.log.error({ err }, 'failed to load portal-integration markdown');
      reply.code(404);
      return { error: 'portal integration guide not available' };
    }
  });
};

function renderRedocPage(specUrl: string): string {
  // Redoc is loaded from the public CDN. We use the standalone bundle so
  // there's zero JS build overhead for the docs page — it's a single
  // <redoc> custom element.
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>PRISM API</title>
  <link rel="icon" type="image/png" href="/favicon.png" />
  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" rel="stylesheet" />
  <style>
    body { margin: 0; font-family: 'Inter', system-ui, sans-serif; }
    .topbar {
      position: sticky; top: 0; z-index: 10;
      display: flex; align-items: center; gap: 12px;
      background: #0e1116; color: #fff;
      padding: 10px 24px;
      border-bottom: 1px solid #222;
      font-size: 14px;
    }
    .topbar .brand-dot {
      width: 10px; height: 10px; border-radius: 50%;
      background: #ff6b1a;
    }
    .topbar .brand { font-weight: 700; letter-spacing: 0.04em; }
    .topbar a { color: #d4d4d8; text-decoration: none; }
    .topbar a:hover { color: #fff; }
    .topbar .spacer { margin-left: auto; }
    .topbar .spec-link {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 12px;
      background: rgba(255,255,255,0.08);
      padding: 4px 8px; border-radius: 4px;
    }
  </style>
</head>
<body>
  <div class="topbar">
    <span class="brand-dot"></span>
    <span class="brand">PRISM API</span>
    <span class="spacer"></span>
    <a href="/docs/portal-integration" style="margin-right:12px">Portal integration &rarr;</a>
    <a class="spec-link" href="${specUrl}" target="_blank" rel="noopener">openapi.json &nearr;</a>
    <a href="/admin/" style="margin-left:12px">Back to admin &rarr;</a>
  </div>
  <redoc spec-url="${specUrl}"
         theme='{"colors":{"primary":{"main":"#ff6b1a"}},"typography":{"fontFamily":"Inter, system-ui, sans-serif","headings":{"fontFamily":"Inter, system-ui, sans-serif","fontWeight":"700"}}}'
         hide-loading
         expand-responses="200,201,202"
         json-sample-expand-level="2"
         path-in-middle-panel>
  </redoc>
  <script src="https://cdn.redoc.ly/redoc/latest/bundles/redoc.standalone.js"></script>
</body>
</html>
`;
}

function renderPortalDocHtml(markdown: string, publicBaseUrl: string): string {
  // markdown-it doesn't ship a default theme; we wrap the rendered body
  // in our own CSS that matches the Redoc page's brand chrome above so
  // operators bouncing between the two pages don't get visual whiplash.
  // Code-block syntax highlighting is left to the browser's default
  // monospace rendering — a CDN-loaded highlighter would be nice but is
  // not load-bearing for the v0.2.0 docs (and pulls another script in).
  const body = md.render(markdown);
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>PRISM Visualiser — Portal Integration</title>
  <link rel="icon" type="image/png" href="/favicon.png" />
  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet" />
  <style>
    :root {
      --bg: #ffffff;
      --fg: #1a1d23;
      --fg-muted: #5b6270;
      --accent: #ff6b1a;
      --border: #e2e6eb;
      --code-bg: #f5f7fa;
      --code-fg: #1a1d23;
      --table-header-bg: #f0f2f5;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0e1116;
        --fg: #e4e6eb;
        --fg-muted: #9ca3af;
        --border: #2a2f37;
        --code-bg: #161a20;
        --code-fg: #e4e6eb;
        --table-header-bg: #1a1f26;
      }
    }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; background: var(--bg); color: var(--fg); }
    body { font-family: 'Inter', system-ui, -apple-system, sans-serif;
           font-size: 16px; line-height: 1.6; }
    .topbar {
      position: sticky; top: 0; z-index: 10;
      display: flex; align-items: center; gap: 12px;
      background: #0e1116; color: #fff;
      padding: 10px 24px;
      border-bottom: 1px solid #222;
      font-size: 14px;
    }
    .topbar .brand-dot { width: 10px; height: 10px; border-radius: 50%; background: var(--accent); }
    .topbar .brand { font-weight: 700; letter-spacing: 0.04em; }
    .topbar a { color: #d4d4d8; text-decoration: none; }
    .topbar a:hover { color: #fff; }
    .topbar .spacer { margin-left: auto; }
    .topbar .spec-link {
      font-family: 'JetBrains Mono', ui-monospace, monospace;
      font-size: 12px;
      background: rgba(255,255,255,0.08);
      padding: 4px 8px; border-radius: 4px;
    }
    main {
      max-width: 880px;
      margin: 0 auto;
      padding: 48px 24px 96px;
    }
    main h1, main h2, main h3, main h4 {
      font-weight: 700;
      letter-spacing: -0.01em;
      line-height: 1.25;
      margin-top: 2em;
      margin-bottom: 0.6em;
    }
    main h1 { font-size: 2.2em; margin-top: 0.2em; }
    main h2 { font-size: 1.55em; border-bottom: 1px solid var(--border); padding-bottom: 0.3em; }
    main h3 { font-size: 1.2em; }
    main h4 { font-size: 1.05em; color: var(--fg-muted); }
    main p, main li { font-size: 1em; }
    main a { color: var(--accent); text-decoration: none; }
    main a:hover { text-decoration: underline; }
    main code {
      font-family: 'JetBrains Mono', ui-monospace, monospace;
      font-size: 0.92em;
      background: var(--code-bg);
      padding: 2px 6px;
      border-radius: 3px;
      color: var(--code-fg);
    }
    main pre {
      background: var(--code-bg);
      color: var(--code-fg);
      padding: 16px 20px;
      border-radius: 6px;
      overflow-x: auto;
      font-family: 'JetBrains Mono', ui-monospace, monospace;
      font-size: 0.88em;
      line-height: 1.5;
      border: 1px solid var(--border);
    }
    main pre code { background: transparent; padding: 0; font-size: inherit; }
    main blockquote {
      margin: 1em 0;
      padding: 0.4em 1em;
      border-left: 4px solid var(--accent);
      color: var(--fg-muted);
      background: var(--code-bg);
    }
    main table {
      border-collapse: collapse;
      width: 100%;
      margin: 1em 0;
      font-size: 0.92em;
    }
    main th, main td {
      border: 1px solid var(--border);
      padding: 8px 12px;
      text-align: left;
      vertical-align: top;
    }
    main th { background: var(--table-header-bg); font-weight: 600; }
    main hr { border: 0; border-top: 1px solid var(--border); margin: 2.5em 0; }
    main img { max-width: 100%; }
  </style>
</head>
<body>
  <div class="topbar">
    <span class="brand-dot"></span>
    <span class="brand">PRISM</span>
    <span class="spacer"></span>
    <a href="/docs" style="margin-right:12px">&larr; OpenAPI spec</a>
    <a class="spec-link" href="/docs/portal-integration.md" target="_blank" rel="noopener">view raw .md &nearr;</a>
    <a href="${publicBaseUrl.replace(/\/+$/, '')}/admin/" style="margin-left:12px">admin &rarr;</a>
  </div>
  <main>
    ${body}
  </main>
</body>
</html>
`;
}

export default plugin;
