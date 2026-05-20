/**
 * Public API documentation surface.
 *
 *  GET /api/openapi.json   -> OpenAPI 3.1 spec for /v1/* (JSON)
 *  GET /docs               -> Redoc-rendered HTML (single page, CDN-loaded)
 *  GET /docs/              -> same (trailing-slash variant for browsers)
 *
 * No authentication. The spec describes how to authenticate (X-API-Key) and
 * exposing it openly is fine — third-party developers need to read it to
 * decide whether to integrate at all.
 */
import type { FastifyPluginAsync } from 'fastify';
import { buildOpenApi } from './openapi.js';

const plugin: FastifyPluginAsync = async (app) => {
  const publicBaseUrl = process.env.PUBLIC_BASE_URL ?? 'https://prism.rebus.industries';

  // ---- machine-readable spec ----
  app.get('/api/openapi.json', async (_req, reply) => {
    reply
      .header('cache-control', 'public, max-age=300')
      .header('access-control-allow-origin', '*');
    return buildOpenApi(publicBaseUrl);
  });

  // ---- human-readable docs (Redoc) ----
  const html = renderRedocPage(`${publicBaseUrl}/api/openapi.json`);
  for (const path of ['/docs', '/docs/']) {
    app.get(path, async (_req, reply) => {
      reply.header('cache-control', 'public, max-age=60');
      reply.type('text/html; charset=utf-8');
      return html;
    });
  }
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
    .topbar a { color: #d4d4d8; text-decoration: none; margin-left: auto; }
    .topbar a:hover { color: #fff; }
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
    <a class="spec-link" href="${specUrl}" target="_blank" rel="noopener">openapi.json &nearr;</a>
    <a href="/admin/">Back to admin &rarr;</a>
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

export default plugin;
