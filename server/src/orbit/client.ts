/**
 * Minimal ORBIT GraphQL client.
 *
 * Used by the admin Settings page (to verify a token works) and the public
 * Convert SPA (to populate project/model dropdowns). We hold the credentials
 * server-side — clients never see the token, they just see results.
 *
 * The schema we target is standard Speckle V2 (ORBIT is a fork of Speckle).
 * If ORBIT ever diverges, only this file needs to change.
 */
import { getSetting, type SettingKey } from '../db/settings.js';

export type OrbitTarget = 'prod' | 'dev';

export interface OrbitCreds {
  url: string;
  token: string;
}

export interface OrbitServerInfo {
  name: string;
  version: string;
  company?: string | null;
}

export interface OrbitUser {
  id: string;
  name: string;
  email?: string | null;
  role?: string | null;
}

export interface OrbitProjectSummary {
  id: string;
  name: string;
  description?: string | null;
  role?: string | null;
  visibility?: string | null;
  updatedAt?: string | null;
}

export interface OrbitModelSummary {
  id: string;
  name: string;
  displayName?: string | null;
  description?: string | null;
  previewUrl?: string | null;
  updatedAt?: string | null;
}

export class OrbitClientError extends Error {
  constructor(public status: number, message: string, public detail?: unknown) {
    super(message);
    this.name = 'OrbitClientError';
  }
}

/**
 * Resolve credentials for the requested target. Returns `null` when the
 * admin hasn't configured a URL or token — callers should surface that to
 * the UI rather than treating it as an error.
 */
export async function getOrbitCreds(target: OrbitTarget): Promise<OrbitCreds | null> {
  const urlKey:   SettingKey = target === 'dev' ? 'orbit_dev_server_url' : 'orbit_server_url';
  const tokenKey: SettingKey = target === 'dev' ? 'orbit_dev_token'      : 'orbit_token';

  const url   = (await getSetting(urlKey))?.trim();
  const token = (await getSetting(tokenKey))?.trim();
  if (!url || !token) return null;
  return { url: url.replace(/\/+$/, ''), token };
}

async function gql<T>(creds: OrbitCreds, query: string, variables?: Record<string, unknown>): Promise<T> {
  let res: Response;
  try {
    res = await fetch(`${creds.url}/graphql`, {
      method: 'POST',
      headers: {
        'content-type': 'application/json',
        authorization: `Bearer ${creds.token}`,
      },
      body: JSON.stringify({ query, variables }),
    });
  } catch (err) {
    throw new OrbitClientError(0, `cannot reach ORBIT at ${creds.url}: ${(err as Error).message}`);
  }

  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new OrbitClientError(res.status, `ORBIT returned ${res.status}`, text);
  }

  let json: { data?: T; errors?: Array<{ message: string }> };
  try {
    json = (await res.json()) as { data?: T; errors?: Array<{ message: string }> };
  } catch {
    throw new OrbitClientError(502, 'ORBIT returned non-JSON');
  }

  if (json.errors?.length) {
    const msg = json.errors.map((e) => e.message).join('; ');
    // First GraphQL error usually tells us if auth was rejected.
    const isAuth = /not authoriz|forbidden|token|unauthor/i.test(msg);
    throw new OrbitClientError(isAuth ? 401 : 400, msg, json.errors);
  }
  if (!json.data) throw new OrbitClientError(502, 'ORBIT response missing data');
  return json.data;
}

const TEST_QUERY = `query Test {
  activeUser { id name email role }
  serverInfo { name version company }
}`;

interface TestResult {
  activeUser: OrbitUser | null;
  serverInfo: OrbitServerInfo;
}

/**
 * Verify that the configured token works for the given target. Returns the
 * authenticated user + serverInfo on success. Throws `OrbitClientError`
 * with a useful status code on any failure.
 */
export async function testConnection(target: OrbitTarget): Promise<{
  ok: true;
  user: OrbitUser;
  serverInfo: OrbitServerInfo;
} | {
  ok: false;
  reason: 'no-creds' | 'no-user';
  serverInfo?: OrbitServerInfo;
}> {
  const creds = await getOrbitCreds(target);
  if (!creds) return { ok: false, reason: 'no-creds' };

  const data = await gql<TestResult>(creds, TEST_QUERY);
  if (!data.activeUser) {
    return { ok: false, reason: 'no-user', serverInfo: data.serverInfo };
  }
  return { ok: true, user: data.activeUser, serverInfo: data.serverInfo };
}

const PROJECTS_QUERY = `query Projects($limit: Int!, $cursor: String) {
  activeUser {
    projects(limit: $limit, cursor: $cursor) {
      totalCount
      cursor
      items { id name description updatedAt role visibility }
    }
  }
}`;

interface ProjectsResult {
  activeUser: { projects: { totalCount: number; cursor: string | null; items: OrbitProjectSummary[] } } | null;
}

/**
 * List projects visible to the configured token's user.
 *
 * The Speckle/ORBIT GraphQL API paginates with cursors. We pull up to
 * `limit` (default 100) projects in a single page; callers that need
 * more can pass an explicit cursor.
 */
export async function listProjects(target: OrbitTarget, opts: { limit?: number; cursor?: string } = {}): Promise<{
  totalCount: number;
  cursor: string | null;
  items: OrbitProjectSummary[];
}> {
  const creds = await getOrbitCreds(target);
  if (!creds) throw new OrbitClientError(412, `ORBIT ${target} credentials not configured`);

  const data = await gql<ProjectsResult>(creds, PROJECTS_QUERY, {
    limit: opts.limit ?? 100,
    cursor: opts.cursor ?? null,
  });
  if (!data.activeUser) throw new OrbitClientError(401, 'ORBIT token has no active user');
  return data.activeUser.projects;
}

const MODELS_QUERY = `query Models($projectId: String!, $limit: Int!, $cursor: String) {
  project(id: $projectId) {
    id
    name
    models(limit: $limit, cursor: $cursor) {
      totalCount
      cursor
      items { id name displayName description previewUrl updatedAt }
    }
  }
}`;

interface ModelsResult {
  project: {
    id: string;
    name: string;
    models: { totalCount: number; cursor: string | null; items: OrbitModelSummary[] };
  } | null;
}

export async function listModels(target: OrbitTarget, projectId: string, opts: { limit?: number; cursor?: string } = {}): Promise<{
  projectName: string;
  totalCount: number;
  cursor: string | null;
  items: OrbitModelSummary[];
}> {
  const creds = await getOrbitCreds(target);
  if (!creds) throw new OrbitClientError(412, `ORBIT ${target} credentials not configured`);

  const data = await gql<ModelsResult>(creds, MODELS_QUERY, {
    projectId,
    limit: opts.limit ?? 200,
    cursor: opts.cursor ?? null,
  });
  if (!data.project) throw new OrbitClientError(404, `project ${projectId} not found`);
  return {
    projectName: data.project.name,
    totalCount: data.project.models.totalCount,
    cursor: data.project.models.cursor,
    items: data.project.models.items,
  };
}

/* -------------------------------------------------------------------------- */
/* Version resolution                                                          */
/* -------------------------------------------------------------------------- */

const LATEST_VERSION_QUERY = `query LatestVersion($projectId: String!, $modelId: String!) {
  project(id: $projectId) {
    model(id: $modelId) {
      versions(limit: 1) {
        items { id referencedObject }
      }
    }
  }
}`;

interface LatestVersionResult {
  project: {
    model: {
      versions: { items: Array<{ id: string; referencedObject: string | null }> };
    } | null;
  } | null;
}

/**
 * Resolve the most recent version id for a model. Used by the visualiser
 * dispatcher to fill in `run.versionId` when the caller omitted it
 * (meaning "use the latest version"). Returns `null` when the model has
 * no versions yet.
 */
export async function getLatestVersionId(
  target: OrbitTarget,
  projectId: string,
  modelId: string,
): Promise<string | null> {
  const creds = await getOrbitCreds(target);
  if (!creds) throw new OrbitClientError(412, `ORBIT ${target} credentials not configured`);

  const data = await gql<LatestVersionResult>(creds, LATEST_VERSION_QUERY, {
    projectId,
    modelId,
  });
  if (!data.project) throw new OrbitClientError(404, `project ${projectId} not found`);
  if (!data.project.model) throw new OrbitClientError(404, `model ${modelId} not found in project ${projectId}`);
  const items = data.project.model.versions.items;
  return items.length > 0 ? (items[0]?.id ?? null) : null;
}

/* -------------------------------------------------------------------------- */
/* Mutations                                                                   */
/* -------------------------------------------------------------------------- */

const CREATE_PROJECT_MUTATION = `mutation CreateProject($name: String!, $description: String) {
  projectMutations {
    create(input: { name: $name, description: $description }) {
      id name description role visibility updatedAt
    }
  }
}`;

interface CreateProjectResult {
  projectMutations: { create: OrbitProjectSummary };
}

export async function createProject(
  target: OrbitTarget,
  name: string,
  description?: string,
): Promise<OrbitProjectSummary> {
  const creds = await getOrbitCreds(target);
  if (!creds) throw new OrbitClientError(412, `ORBIT ${target} credentials not configured`);
  const data = await gql<CreateProjectResult>(creds, CREATE_PROJECT_MUTATION, {
    name,
    description: description ?? null,
  });
  return data.projectMutations.create;
}

const CREATE_MODEL_MUTATION = `mutation CreateModel($input: CreateModelInput!) {
  modelMutations {
    create(input: $input) {
      id name displayName description previewUrl updatedAt
    }
  }
}`;

interface CreateModelResult {
  modelMutations: { create: OrbitModelSummary };
}

export async function createModel(
  target: OrbitTarget,
  projectId: string,
  name: string,
  description?: string,
): Promise<OrbitModelSummary> {
  const creds = await getOrbitCreds(target);
  if (!creds) throw new OrbitClientError(412, `ORBIT ${target} credentials not configured`);
  const data = await gql<CreateModelResult>(creds, CREATE_MODEL_MUTATION, {
    input: { projectId, name, description: description ?? null },
  });
  return data.modelMutations.create;
}
