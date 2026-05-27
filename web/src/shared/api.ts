/**
 * Typed REST client. Wraps fetch + tiny error normalisation; all PRISM
 * SPAs use it so we don't sprinkle ad-hoc URL strings everywhere.
 */

export interface ApiError {
  status: number;
  message: string;
  body?: unknown;
}

// ---------------------------------------------------------------------------
// API call logging — records every fetch through ApiClient so the admin SPA
// can render a live log panel. Lightweight in-memory ring buffer + simple
// pub/sub. Bodies are JSON-stringified (truncated) for display; FormData
// requests log just the field names + file sizes.
// ---------------------------------------------------------------------------

export interface ApiLogEntry {
  id: number;
  startedAt: number;          // epoch ms
  durationMs: number;
  method: string;
  url: string;
  status: number;             // 0 if network failure
  ok: boolean;
  requestBody?: string;
  responseBody?: string;
  errorMessage?: string;
}

const MAX_LOG_ENTRIES = 250;
const MAX_BODY_PREVIEW = 4000;
let nextLogId = 1;

class ApiLog {
  private entries: ApiLogEntry[] = [];
  private listeners = new Set<(entries: ApiLogEntry[]) => void>();

  list(): ApiLogEntry[] { return this.entries; }

  push(entry: ApiLogEntry): void {
    this.entries = [entry, ...this.entries].slice(0, MAX_LOG_ENTRIES);
    for (const fn of this.listeners) fn(this.entries);
  }

  clear(): void {
    this.entries = [];
    for (const fn of this.listeners) fn(this.entries);
  }

  subscribe(fn: (entries: ApiLogEntry[]) => void): () => void {
    this.listeners.add(fn);
    fn(this.entries);
    return () => this.listeners.delete(fn);
  }
}

export const apiLog = new ApiLog();

function previewBody(body: BodyInit | null | undefined): string | undefined {
  if (body == null) return undefined;
  if (typeof body === 'string') return truncate(body);
  if (typeof FormData !== 'undefined' && body instanceof FormData) {
    const parts: string[] = [];
    body.forEach((v, k) => {
      if (typeof File !== 'undefined' && v instanceof File) {
        parts.push(`${k}=<file ${v.name} ${v.size}B ${v.type || '?'}>`);
      } else {
        parts.push(`${k}=${truncate(String(v), 200)}`);
      }
    });
    return `FormData { ${parts.join(', ')} }`;
  }
  try { return truncate(JSON.stringify(body)); } catch { return '<unserialisable>'; }
}

function truncate(s: string, max = MAX_BODY_PREVIEW): string {
  if (s.length <= max) return s;
  return s.slice(0, max) + ` …(+${s.length - max} bytes)`;
}

export type JobStatus =
  | 'queued'
  | 'dispatched'
  | 'awaiting_selection'
  | 'processing'
  | 'uploading'
  | 'complete'
  | 'failed'
  | 'cancelled';

export interface LayerNode {
  name: string;
  fullPath?: string;
  color?: string;
  visible?: boolean;
  children?: LayerNode[];
}

export interface JobSummary {
  id: string;
  status: JobStatus;
  jobType?: 'convert' | 'receive';
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
  fileName: string;
  fileSize: number;
  format: string;
  orbitTarget: 'prod' | 'dev';
  projectId: string;
  modelId: string;
  modelName?: string | null;
  nodeName?: string | null;
  currentStage?: string | null;
  progressPercent?: number | null;
  lastMessage?: string | null;
  resultUrl?: string | null;
  rootObjectId?: string | null;
  versionId?: string | null;
  outputFormats?: string[] | null;
  outputs?: Record<string, string> | null;
  receiveVersionId?: string | null;
  error?: string | null;
  // Two-phase layer-selection flow:
  selectLayers?: boolean;
  includedLayers?: string[];
  includeLayerDescendants?: boolean;
  hasLayers?: boolean;
}

export interface Workstation {
  id: string;
  machineId: string;
  nodeName: string;
  canConvert: boolean;
  canLayer: boolean;
  canReceive: boolean;
  supportedFormats: string[];
  slotsTotal: number;
  agentVersion?: string | null;
  rhinoVersion?: string | null;
  isEnabled: boolean;
  notes?: string | null;
  createdAt: string;
  lastSeenAt?: string | null;
  online?: boolean;
  slotsBusy?: number;
  sessions?: number;
}

export interface ApiKey {
  id: string;
  name: string;
  isActive: boolean;
  rateLimitPerMin?: number | null;
  monthlyQuota?: number | null;
  createdAt: string;
  lastUsedAt?: string | null;
}

class ApiClient {
  constructor(private base: string = '') {}

  private async req<T>(path: string, init: RequestInit = {}): Promise<T> {
    const startedAt = Date.now();
    const method = (init.method ?? 'GET').toUpperCase();
    const url = this.base + path;
    const requestBody = previewBody(init.body);

    let res: Response;
    try {
      res = await fetch(url, {
        credentials: 'include',
        headers: { accept: 'application/json', ...(init.headers ?? {}) },
        ...init,
      });
    } catch (netErr) {
      const message = netErr instanceof Error ? netErr.message : String(netErr);
      apiLog.push({
        id: nextLogId++, startedAt, durationMs: Date.now() - startedAt,
        method, url, status: 0, ok: false, requestBody, errorMessage: message,
      });
      throw { status: 0, message, body: undefined } satisfies ApiError;
    }

    const ct = res.headers.get('content-type') ?? '';
    if (!res.ok) {
      let body: unknown;
      try { body = await res.json(); } catch { body = await res.text().catch(() => ''); }
      const err: ApiError = { status: res.status, message: extractMessage(body) ?? res.statusText, body };
      apiLog.push({
        id: nextLogId++, startedAt, durationMs: Date.now() - startedAt,
        method, url, status: res.status, ok: false, requestBody,
        responseBody: previewBody(typeof body === 'string' ? body : safeJson(body)),
        errorMessage: err.message,
      });
      throw err;
    }

    const isJson = ct.includes('application/json');
    const parsed = (isJson ? await res.json() : await res.text()) as unknown;
    apiLog.push({
      id: nextLogId++, startedAt, durationMs: Date.now() - startedAt,
      method, url, status: res.status, ok: true, requestBody,
      responseBody: previewBody(isJson ? safeJson(parsed) : (parsed as string)),
    });
    return parsed as T;
  }

  get<T>(path: string)  { return this.req<T>(path, { method: 'GET' }); }
  delete<T>(path: string) { return this.req<T>(path, { method: 'DELETE' }); }
  put<T>(path: string, body: unknown) {
    return this.req<T>(path, { method: 'PUT', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  }
  patch<T>(path: string, body: unknown) {
    return this.req<T>(path, { method: 'PATCH', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  }
  post<T>(path: string, body: unknown) {
    return this.req<T>(path, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
  }

  postForm<T>(path: string, form: FormData) {
    return this.req<T>(path, { method: 'POST', body: form });
  }
}

function safeJson(value: unknown): string {
  try { return JSON.stringify(value); } catch { return String(value); }
}

function extractMessage(body: unknown): string | undefined {
  if (typeof body === 'string') return body || undefined;
  if (body && typeof body === 'object') {
    const o = body as Record<string, unknown>;
    if (typeof o['error'] === 'string') return o['error'];
    if (typeof o['message'] === 'string') return o['message'];
  }
  return undefined;
}

export const api = new ApiClient('');

export interface LayersResponse {
  jobId: string;
  status: JobStatus;
  layers: LayerNode[];
  includedLayers: string[];
  includeLayerDescendants: boolean;
}

/**
 * One streaming log line attached to a job.  The backend writes these
 * via {@link jobLogs} from both the server itself (lifecycle events,
 * dispatcher decisions) and from agents over WebSocket (per-stage
 * progress, IronPython output).  Returned by `GET /api/jobs/:id/logs`.
 */
export interface JobLogLine {
  id: number;
  jobId: string;
  ts: string;            // ISO timestamp from Postgres (drizzle serialises Date -> string here)
  level: string;         // 'debug' | 'info' | 'warn' | 'error' (free-form 8 chars)
  source: 'server' | 'agent' | string;
  message: string;
}

// Sugar for the common endpoints — typed responses
export const jobsApi = {
  list:   (params?: { status?: string; limit?: number; offset?: number }) => {
    const qs = new URLSearchParams();
    if (params?.status) qs.set('status', params.status);
    if (params?.limit !== undefined)  qs.set('limit', String(params.limit));
    if (params?.offset !== undefined) qs.set('offset', String(params.offset));
    return api.get<{ jobs: JobSummary[]; limit: number; offset: number }>(`/api/jobs?${qs}`);
  },
  get:    (id: string) => api.get<JobSummary>(`/api/jobs/${id}`),
  remove: (id: string) => api.delete<{ deleted: string }>(`/api/jobs/${id}`),
  // Two-phase layer-selection flow:
  getLayers:    (id: string) => api.get<LayersResponse>(`/api/jobs/${id}/layers`),
  submitLayers: (id: string, body: { includedLayers: string[]; includeLayerDescendants: boolean }) =>
    api.post<{ jobId: string; status: JobStatus; includedLayers: string[]; includeLayerDescendants: boolean }>(
      `/api/jobs/${id}/layers`,
      body,
    ),
  // Per-job server + agent log lines (drives JobLogsModal in the admin UI).
  getLogs: (id: string) => api.get<{ logs: JobLogLine[] }>(`/api/jobs/${id}/logs`),
};

export const workstationsApi = {
  list:   () => api.get<{ workstations: Workstation[] }>('/api/workstations'),
  get:    (id: string) => api.get<Workstation>(`/api/workstations/${id}`),
  update: (id: string, body: Partial<Workstation>) => api.patch<Workstation>(`/api/workstations/${id}`, body),
  remove: (id: string) => api.delete<{ deleted: string }>(`/api/workstations/${id}`),

  // ---------------------------------------------- node provisioning downloads
  // Since agent v0.1.30 ships a wizard installer (`.exe`) that embeds the
  // PowerShell install scripts and prompts for prismUrl/nodeName/slots,
  // the server only needs to expose the latest installer; the older
  // /install-script and /agent-config endpoints are gone.
  agentInfo: () => api.get<AgentBuildInfo>('/api/admin/workstations/downloads/agent'),
  agentDownloadUrl: () => '/api/admin/workstations/downloads/agent/download',
  /** Hard-coded GitHub releases page for the agent — used as the
   *  "View on GitHub" link next to the download button. */
  releasesPageUrl: 'https://github.com/REBUS-ORBIT/prism-agent/releases/latest',
};

export interface AgentBuildInfo {
  downloadUrl: string | null;
  version: string | null;
  wsUrl: string;
  available: boolean;
  buildSource: {
    workflow: string;
    artifact: string;
    howTo: string;
  };
}

export const keysApi = {
  list:   () => api.get<{ keys: ApiKey[] }>('/api/keys'),
  create: (body: { name: string; rateLimitPerMin?: number; monthlyQuota?: number }) =>
            api.post<{ plaintext: string; key: ApiKey }>('/api/keys', body),
  patch:  (id: string, body: { isActive?: boolean }) => api.patch<{ ok: true }>(`/api/keys/${id}`, body),
  remove: (id: string) => api.delete<{ deleted: string }>(`/api/keys/${id}`),
};

export const settingsApi = {
  list: () => api.get<{ settings: Record<string, string> }>('/api/settings'),
  set:  (key: string, value: string) => api.put<{ ok: true }>(`/api/settings/${encodeURIComponent(key)}`, { value }),
};

export const adminApi = {
  me:     () => api.get<{ kind: string; principal: { username?: string } }>('/api/admin/me'),
  login:  (username: string, password: string) => api.post<{ ok: true; username: string }>('/api/admin/login', { username, password }),
  logout: () => api.post<{ ok: true }>('/api/admin/logout', {}),
  changePassword: (currentPassword: string, newPassword: string) =>
    api.post<{ ok: true }>('/api/admin/change-password', { currentPassword, newPassword }),
  cancelJob: (id: string) => api.post<{ cancelled: boolean }>(`/api/jobs/${id}/cancel`, {}),
};

export interface PipelineNode {
  id: string;
  kind: string;
  label: string;
  description: string;
  optional?: boolean;
}
export interface PipelineEdge { from: string; to: string; label?: string; }
export interface PipelineTopology { nodes: PipelineNode[]; edges: PipelineEdge[]; }

export const pipelinesApi = {
  list: () => api.get<{ pipelines: Record<string, PipelineTopology> }>('/api/pipelines'),
  get:  (id: string) => api.get<PipelineTopology>(`/api/pipelines/${id}`),
};

export const convertApi = {
  submit: (file: File, opts: {
    projectId: string;
    modelId: string;
    modelName?: string;
    orbitTarget?: 'prod' | 'dev';
    swapYZ?: boolean;
    quality?: 'sensible' | 'extreme';
    callbackUrl?: string;
    includedLayers?: string[];
    includeLayerDescendants?: boolean;
    /** Two-phase flow: ask agent for layer tree before conversion. */
    selectLayers?: boolean;
  }) => {
    const fd = new FormData();
    fd.append('file', file);
    fd.append('projectId', opts.projectId);
    fd.append('modelId',   opts.modelId);
    if (opts.modelName)   fd.append('modelName',   opts.modelName);
    if (opts.orbitTarget) fd.append('orbitTarget', opts.orbitTarget);
    if (opts.swapYZ !== undefined)   fd.append('swapYZ', String(opts.swapYZ));
    if (opts.quality)                fd.append('quality', opts.quality);
    if (opts.callbackUrl)            fd.append('callbackUrl', opts.callbackUrl);
    if (opts.includedLayers?.length) fd.append('includedLayers', opts.includedLayers.join(','));
    if (opts.includeLayerDescendants !== undefined) fd.append('includeLayerDescendants', String(opts.includeLayerDescendants));
    if (opts.selectLayers !== undefined) fd.append('selectLayers', String(opts.selectLayers));
    return api.postForm<{ jobId: string; status: string }>('/api/convert/async', fd);
  },
};

export interface Webhook {
  id: string;
  name: string;
  url: string;
  events: string[];
  isActive: boolean;
  createdAt: string;
  secret?: string;       // only present in the create response
}

export const webhooksApi = {
  list:   () => api.get<{ webhooks: Webhook[] }>('/api/webhooks'),
  create: (body: { name: string; url: string; events?: string[] }) => api.post<Webhook>('/api/webhooks', body),
  patch:  (id: string, body: Partial<Pick<Webhook, 'name'|'url'|'events'|'isActive'>> & { regenerateSecret?: boolean }) =>
            api.patch<Webhook>(`/api/webhooks/${id}`, body),
  remove: (id: string) => api.delete<{ deleted: string }>(`/api/webhooks/${id}`),
};

export const receiveApi = {
  submit: (body: {
    projectId: string;
    modelId: string;
    versionId: string;
    modelName?: string;
    orbitTarget?: 'prod' | 'dev';
    outputFormat?: '3dm' | 'step';
    callbackUrl?: string;
  }) => api.post<{ jobId: string; status: string }>('/api/receive/async', body),
};

// ---------------------------------------------------------------- ORBIT lookups
export interface OrbitServerInfo { name: string; version: string; company?: string | null; }
export interface OrbitUser       { id: string; name: string; email?: string | null; role?: string | null; }
export interface OrbitProject {
  id: string;
  name: string;
  description?: string | null;
  role?: string | null;
  visibility?: string | null;
  updatedAt?: string | null;
}
export interface OrbitModel {
  id: string;
  name: string;
  displayName?: string | null;
  description?: string | null;
  previewUrl?: string | null;
  updatedAt?: string | null;
}

export interface OrbitTestOk   { ok: true;  target: 'prod' | 'dev'; user: OrbitUser; serverInfo: OrbitServerInfo; }
export interface OrbitTestFail { ok: false; target: 'prod' | 'dev'; reason: 'no-creds' | 'no-user'; error: string; serverInfo?: OrbitServerInfo; }

export const orbitApi = {
  /** Test stored credentials for a target. Returns either `{ ok: true, user, serverInfo }` or `{ ok: false, reason, error }`. */
  test: async (target: 'prod' | 'dev'): Promise<OrbitTestOk | OrbitTestFail> => {
    try {
      return await api.get<OrbitTestOk>(`/api/orbit/test?target=${target}`);
    } catch (err) {
      const e = err as ApiError;
      const body = (e.body as Partial<OrbitTestFail>) ?? {};
      return {
        ok: false,
        target,
        reason: body.reason ?? 'no-user',
        error: body.error ?? e.message,
        serverInfo: body.serverInfo,
      };
    }
  },
  projects: (target: 'prod' | 'dev', limit = 100) =>
    api.get<{ target: string; totalCount: number; cursor: string | null; items: OrbitProject[] }>(
      `/api/orbit/projects?target=${target}&limit=${limit}`,
    ),
  models: (target: 'prod' | 'dev', projectId: string, limit = 200) =>
    api.get<{ target: string; projectId: string; projectName: string; totalCount: number; cursor: string | null; items: OrbitModel[] }>(
      `/api/orbit/projects/${encodeURIComponent(projectId)}/models?target=${target}&limit=${limit}`,
    ),
  createProject: (target: 'prod' | 'dev', name: string) =>
    api.post<{ target: string; project: OrbitProject }>('/api/orbit/projects', { target, name }),
  createModel: (target: 'prod' | 'dev', projectId: string, name: string) =>
    api.post<{ target: string; projectId: string; model: OrbitModel }>(
      `/api/orbit/projects/${encodeURIComponent(projectId)}/models`,
      { target, name },
    ),
};
