/**
 * /api/admin/workstations/downloads — node provisioning artifacts.
 *
 * Surfaces everything an admin needs to bring a new Rhino workstation
 * online: the prebuilt PRISM.Agent payload (zip from .github/workflows/agent.yml),
 * the helper PowerShell install scripts (bundled into the server image
 * from agent/install/), and a per-node `agent-config.json` template
 * pre-filled with the right WSS endpoint.
 *
 * The MSI/zip itself lives as a GitHub Release asset on REBUS-ORBIT/prism-agent.
 * The server first checks the DB for a manually set URL; if absent, it falls
 * back to the GitHub Releases API so the workstation download page always
 * shows the current version without requiring the CI runner to SSH into the VM.
 */
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import type { FastifyPluginAsync } from 'fastify';
import { requireAdmin } from '../auth/middleware.js';
import { getSetting } from '../db/settings.js';

const GITHUB_RELEASE_REPO = 'REBUS-ORBIT/prism-agent';
const GITHUB_RELEASE_ASSET_PATTERN = /^PRISM\.Agent-.+\.zip$/;

interface GitHubReleaseInfo {
  version: string;
  downloadUrl: string;
}

// Simple 1-hour in-memory cache so we don't hammer the GitHub API on every page load.
let _releaseCache: { data: GitHubReleaseInfo; expiresAt: number } | null = null;

async function fetchLatestAgentRelease(): Promise<GitHubReleaseInfo | null> {
  const now = Date.now();
  if (_releaseCache && _releaseCache.expiresAt > now) return _releaseCache.data;

  try {
    const res = await fetch(
      `https://api.github.com/repos/${GITHUB_RELEASE_REPO}/releases/latest`,
      {
        headers: {
          Accept: 'application/vnd.github+json',
          'User-Agent': 'PRISM-Server/1.0',
          'X-GitHub-Api-Version': '2022-11-28',
        },
        signal: AbortSignal.timeout(8000),
      },
    );
    if (!res.ok) return null;
    const json = (await res.json()) as {
      tag_name: string;
      assets: { name: string; browser_download_url: string }[];
    };
    const asset = json.assets?.find((a) => GITHUB_RELEASE_ASSET_PATTERN.test(a.name));
    if (!asset) return null;
    const data: GitHubReleaseInfo = {
      version: json.tag_name,
      downloadUrl: asset.browser_download_url,
    };
    _releaseCache = { data, expiresAt: now + 60 * 60 * 1000 };
    return data;
  } catch {
    return null;
  }
}

const INSTALL_DIR = resolve(process.env.AGENT_INSTALL_DIR ?? './agent-install');

/** Bytes of a static asset under AGENT_INSTALL_DIR, or null if missing. */
async function readInstallAsset(name: string): Promise<Buffer | null> {
  const path = resolve(INSTALL_DIR, name);
  if (!path.startsWith(INSTALL_DIR + (process.platform === 'win32' ? '\\' : '/')) && path !== INSTALL_DIR) {
    return null;
  }
  if (!existsSync(path)) return null;
  return readFile(path);
}

/** Compute the WSS URL the agent should use for this server, honouring the
 *  admin override and falling back to the request's host. Caddy fronts the
 *  prod deployment and terminates TLS, so we must look at x-forwarded-proto
 *  (and x-forwarded-host) before falling back to Fastify's direct values. */
async function resolveAgentWsUrl(req: {
  hostname?: string;
  protocol?: string;
  headers: Record<string, string | string[] | undefined>;
}): Promise<string> {
  const override = (await getSetting('workstation_agent_ws_url'))?.trim();
  if (override) return override;
  const xfHost  = pickFirstHeader(req.headers['x-forwarded-host']);
  const xfProto = pickFirstHeader(req.headers['x-forwarded-proto']);
  const host = (xfHost ?? req.hostname ?? '').trim() || 'prism.rebus.industries';
  const proto = (xfProto ?? req.protocol ?? '').trim();
  const scheme = proto === 'http' ? 'ws' : 'wss';
  return `${scheme}://${host}/ws/agent`;
}

function pickFirstHeader(value: string | string[] | undefined): string | undefined {
  if (Array.isArray(value)) return value[0];
  if (typeof value === 'string') return value.split(',')[0]?.trim();
  return undefined;
}

const plugin: FastifyPluginAsync = async (app) => {
  app.addHook('preHandler', requireAdmin);

  /**
   * GET /agent — meta JSON describing the latest agent build.
   *
   * Resolution order for version + downloadUrl:
   *   1. DB settings (set manually or by the CI SSH step when the runner
   *      can reach the VM — currently the runner is on the public internet
   *      so the SSH step is non-fatal and may not update the DB).
   *   2. GitHub Releases API for REBUS-ORBIT/prism-agent (cached 1 h).
   *   3. null / available: false so the UI renders the "build pending" state.
   */
  app.get('/agent', async (req) => {
    let downloadUrl = (await getSetting('workstation_agent_download_url'))?.trim() || null;
    let version     = (await getSetting('workstation_agent_version'))?.trim()      || null;

    if (!downloadUrl || !version) {
      const ghRelease = await fetchLatestAgentRelease();
      if (ghRelease) {
        downloadUrl = downloadUrl ?? ghRelease.downloadUrl;
        version     = version     ?? ghRelease.version;
      }
    }

    const wsUrl = await resolveAgentWsUrl(req);
    return {
      downloadUrl,
      version,
      wsUrl,
      available: !!downloadUrl,
      buildSource: {
        workflow: '.github/workflows/agent.yml',
        artifact: 'PRISM.Agent-<tag>.zip',
        howTo: 'Tag a release (vX.Y.Z) or trigger the agent-msi workflow manually.',
      },
    };
  });

  /**
   * GET /agent/download — 302 redirect to the configured zip URL. We
   * could proxy the bytes here, but a redirect keeps PRISM out of the
   * upload path and lets GitHub serve at its full bandwidth.
   */
  app.get('/agent/download', async (_req, reply) => {
    const url = (await getSetting('workstation_agent_download_url'))?.trim();
    if (!url) {
      return reply.code(404).send({
        error: 'no agent build configured',
        hint: 'set workstation_agent_download_url in Settings',
      });
    }
    return reply.redirect(url, 302);
  });

  /**
   * GET /install-script[?which=install|uninstall] — returns the bundled
   * PowerShell installer as text/plain; the UI links to it directly with
   * the right Content-Disposition so the browser saves it as install.ps1.
   */
  app.get<{ Querystring: { which?: string } }>('/install-script', async (req, reply) => {
    const which = req.query.which === 'uninstall' ? 'uninstall.ps1' : 'install.ps1';
    const bytes = await readInstallAsset(which);
    if (!bytes) {
      return reply.code(404).send({ error: `${which} not bundled in this server image` });
    }
    reply.header('content-type', 'text/plain; charset=utf-8');
    reply.header('content-disposition', `attachment; filename="${which}"`);
    return bytes;
  });

  /**
   * GET /agent-config?nodeName=...&slots=...&roles=conversion,layering
   * Returns a JSON config the admin can drop into
   * `C:\Program Files\PRISM.Agent\agent-config.json`. We do NOT mint an
   * API key here — the agent currently authenticates by machineId on
   * first connect (see ws/gateway.ts) and the phase-8 enrollment-token
   * flow is still pending. A `TODO: enrollment token` is surfaced in the
   * response so callers know the format will grow a `token` field once
   * that lands.
   */
  app.get<{ Querystring: { nodeName?: string; slots?: string; roles?: string } }>(
    '/agent-config',
    async (req, reply) => {
      const nodeName = (req.query.nodeName ?? '').trim() || 'RB-NEW-NODE';
      const slotsRaw = Number(req.query.slots ?? '2');
      const slots = Number.isFinite(slotsRaw) && slotsRaw > 0 ? Math.floor(slotsRaw) : 2;
      const rolesParam = (req.query.roles ?? 'conversion,layering,receive')
        .split(',').map((s) => s.trim()).filter(Boolean);
      const validRoles = new Set(['conversion', 'layering', 'receive']);
      const roles = rolesParam.filter((r) => validRoles.has(r));
      if (roles.length === 0) roles.push('conversion', 'layering');

      const wsUrl = await resolveAgentWsUrl(req);

      const config = {
        prismUrl: wsUrl,
        nodeName,
        machineId: 'auto',
        slots,
        roles,
        rhinoExecutablePath: 'C:\\Program Files\\Rhino 8\\System\\Rhino.exe',
        logDir: 'C:\\ProgramData\\PRISM.Agent\\logs',
        // TODO(phase-8): when agent enrollment tokens land, surface
        // a freshly-minted token here so the agent can auth on connect.
      };

      reply.header('content-type', 'application/json; charset=utf-8');
      reply.header('content-disposition', `attachment; filename="agent-config-${nodeName}.json"`);
      return config;
    },
  );
};

export default plugin;
