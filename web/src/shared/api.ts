/**
 * Typed REST client. Wraps fetch + tiny error normalisation; all PRISM
 * SPAs use it so we don't sprinkle ad-hoc URL strings everywhere.
 */

export interface ApiError {
  status: number;
  message: string;
  body?: unknown;
}

export interface JobSummary {
  id: string;
  status: 'queued' | 'dispatched' | 'processing' | 'complete' | 'failed' | 'cancelled';
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
    const res = await fetch(this.base + path, {
      credentials: 'include',
      headers: { accept: 'application/json', ...(init.headers ?? {}) },
      ...init,
    });
    if (!res.ok) {
      let body: unknown;
      try { body = await res.json(); } catch { body = await res.text().catch(() => ''); }
      const err: ApiError = { status: res.status, message: extractMessage(body) ?? res.statusText, body };
      throw err;
    }
    const ct = res.headers.get('content-type') ?? '';
    return (ct.includes('application/json') ? res.json() : (res.text() as unknown)) as Promise<T>;
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
};

export const workstationsApi = {
  list:   () => api.get<{ workstations: Workstation[] }>('/api/workstations'),
  get:    (id: string) => api.get<Workstation>(`/api/workstations/${id}`),
  update: (id: string, body: Partial<Workstation>) => api.patch<Workstation>(`/api/workstations/${id}`, body),
  remove: (id: string) => api.delete<{ deleted: string }>(`/api/workstations/${id}`),
};

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
    return api.postForm<{ jobId: string; status: string }>('/api/convert/async', fd);
  },
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
