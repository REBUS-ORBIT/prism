/**
 * Job dispatcher.
 *
 * Given a queued job, find an eligible agent slot and push the appropriate
 * envelope over its WS:
 *
 *   - `assign`     for regular convert / receive jobs
 *   - `pollLayers` for the first phase of a two-phase layer-selection
 *                  convert (job.selectLayers=true && job.layersJson IS NULL)
 *
 * Returns true if dispatched, false if no eligible agent was available
 * (the worker should requeue or hold).
 *
 * Eligibility:
 *   - workstation.is_enabled = true
 *   - workstation.can_convert = true (for convert jobs)
 *   - workstation.can_layer   = true (for pollLayers jobs)
 *   - workstation.can_receive = true (for receive jobs)
 *   - workstation.supported_formats includes job.format
 *   - agent has at least one free slot (slotsBusy < slotsTotal)
 */
import { randomUUID } from 'node:crypto';
import type { FastifyBaseLogger } from 'fastify';
import { eq } from 'drizzle-orm';
import { db } from '../db/client.js';
import { jobs, workstations } from '../db/schema.js';
import { getSetting } from '../db/settings.js';
import { envelope, type AssignData, type PollLayersData } from '../../../shared/contracts/agent-protocol.js';
import { sessionRegistry, type AgentConn } from '../ws/sessionRegistry.js';
import { broadcastJobUpdate } from '../ws/adminProtocol.js';
import { issueDownloadToken } from '../api/internal.js';

const PUBLIC_BASE_URL =
  process.env.PUBLIC_BASE_URL
  ?? process.env.PRISM_PUBLIC_URL
  ?? 'http://localhost:8765';

export interface DispatchOutcome {
  dispatched: boolean;
  agentSessionId?: string;
  nodeName?: string;
  reason?: string;
}

export async function tryDispatch(jobId: string, log: FastifyBaseLogger): Promise<DispatchOutcome> {
  const job = await db.query.jobs.findFirst({ where: eq(jobs.id, jobId) });
  if (!job) return { dispatched: false, reason: 'job not found' };
  if (job.status !== 'queued' && job.status !== 'dispatched') {
    return { dispatched: false, reason: `job is ${job.status}` };
  }

  const isReceive = job.jobType === 'receive';
  // A pollLayers dispatch is the first phase of the two-phase
  // layer-selection flow. Once the agent replies with `layers`, the job
  // moves to `awaiting_selection`; when the caller POSTs the chosen
  // layers the job re-enters this dispatcher with layersJson set, at
  // which point we fall through to the regular convert path.
  const isPollLayers = !isReceive && !!job.selectLayers && !job.layersJson;

  // Resolve workstations table once; live conn state from sessionRegistry.
  const wsRows = await db.select().from(workstations);
  const wsByMachine = new Map(wsRows.map((w) => [w.machineId, w]));

  const eligible: AgentConn[] = [];
  for (const conn of sessionRegistry.allAgents()) {
    const w = wsByMachine.get(conn.machineId);
    if (!w || !w.isEnabled) continue;
    if (isReceive) {
      if (!w.canReceive) continue;
    } else if (isPollLayers) {
      if (!w.canLayer) continue;
    } else {
      if (!w.canConvert) continue;
    }
    // For receive jobs, supportedFormats gates the OUTPUT format (e.g. '3dm');
    // for convert / pollLayers it gates the INPUT format.
    const supported = (w.supportedFormats as string[] | null) ?? [];
    if (!isReceive && !supported.includes(job.format)) continue;
    if (conn.slotsBusy >= conn.hello.slots) continue;
    eligible.push(conn);
  }

  if (eligible.length === 0) {
    return { dispatched: false, reason: 'no eligible agent available' };
  }

  // Trivial selection: least-loaded first, then earliest connected.
  eligible.sort((a, b) =>
    a.slotsBusy !== b.slotsBusy
      ? a.slotsBusy - b.slotsBusy
      : a.connectedAt.getTime() - b.connectedAt.getTime()
  );
  const agent = eligible[0]!;

  const orbitServerUrl = await getSetting(job.orbitTarget === 'dev' ? 'orbit_dev_server_url' : 'orbit_server_url');
  if (!orbitServerUrl && !isPollLayers) {
    // pollLayers doesn't actually need ORBIT creds — but we still resolve
    // them for an eventual convert phase to fail loudly here rather than
    // later.
    log.error({ orbitTarget: job.orbitTarget }, 'dispatch failed: no ORBIT server URL configured for target');
    return { dispatched: false, reason: `no ORBIT URL set for target=${job.orbitTarget}` };
  }

  const orbitToken =
    job.submittedBy?.startsWith('orbit:')
      // For Phase 3 we don't yet persist the bearer that was used at submit time.
      // The convert/async route in Phase 7 will stash it (encrypted) on the job row.
      ? (await getSetting('orbit_token')) ?? ''
      : (await getSetting('orbit_token')) ?? '';
  if (!orbitToken && !isPollLayers) {
    log.warn({ jobId }, 'no shared orbit_token; agent will get an empty bearer (Phase 7 fixes per-user tokens)');
  }

  // pollLayers and convert both need a download URL; receive does not.
  let fileUrl: string | undefined;
  if (!isReceive) {
    const fileToken = await issueDownloadToken(job.id);
    fileUrl = `${PUBLIC_BASE_URL.replace(/\/$/, '')}/internal/files/${job.id}?token=${fileToken}`;
  }

  if (isPollLayers) {
    const poll: PollLayersData = {
      jobId: job.id,
      fileUrl: fileUrl!,
      format: job.format,
    };
    try {
      agent.socket.send(JSON.stringify(envelope('pollLayers', poll, randomUUID())));
      agent.slotsBusy += 1;
    } catch (err) {
      log.warn({ err, agentSessionId: agent.sessionId }, 'agent ws send (pollLayers) failed; will requeue');
      return { dispatched: false, reason: 'agent send failed' };
    }

    await db
      .update(jobs)
      .set({
        status: 'dispatched',
        nodeName: agent.nodeName,
        agentSessionId: agent.sessionId,
        currentStage: 'polling-layers',
        lastMessage: 'extracting layer tree on agent',
        updatedAt: new Date(),
      })
      .where(eq(jobs.id, job.id));

    broadcastJobUpdate(job.id, {
      status: 'dispatched',
      currentStage: 'polling-layers',
      lastMessage: 'extracting layer tree on agent',
      nodeName: agent.nodeName,
    });

    log.info({ jobId: job.id, nodeName: agent.nodeName, sessionId: agent.sessionId }, 'job dispatched as pollLayers');
    return { dispatched: true, agentSessionId: agent.sessionId, nodeName: agent.nodeName };
  }

  // Always provide an upload-back URL so the agent can deliver non-ORBIT outputs
  // (3DM, GLB, IFC, STEP, or the receive primary).
  const outputBaseUrl = `${PUBLIC_BASE_URL.replace(/\/$/, '')}/internal/outputs/${job.id}`;
  const outputFormats = (job.outputFormats as string[] | null) ?? [];

  // Compose the AssignOptions, expanding includedLayers with the descendant
  // set if requested AND we have a layer tree to expand from (two-phase
  // flow). Direct callers that pass `includedLayers` + `includeLayerDescendants`
  // without going through pollLayers get the agent-side fallback expansion in
  // ConvertJob.AssignToCard.
  const persistedOptions = (job.options as AssignData['options']) ?? {};
  const includedLayers = (job.includedLayers as string[] | null) ?? persistedOptions.includedLayers ?? [];
  const includeLayerDescendants = job.includeLayerDescendants ?? persistedOptions.includeLayerDescendants ?? false;
  const expandedLayers = includeLayerDescendants && job.layersJson
    ? expandLayerSelection(includedLayers, job.layersJson as LayerNode[])
    : includedLayers;

  const options: AssignData['options'] = {
    ...persistedOptions,
    includedLayers: expandedLayers.length ? expandedLayers : undefined,
    includeLayerDescendants: includeLayerDescendants || undefined,
  };

  const assign: AssignData = {
    jobId: job.id,
    jobType: isReceive ? 'receive' : 'convert',
    slot: agent.slotsBusy,
    format: job.format,
    fileUrl,
    fileName: job.fileName,
    orbitServerUrl: orbitServerUrl ?? '',
    orbitToken,
    projectId: job.projectId,
    modelId: job.modelId,
    modelName: job.modelName ?? undefined,
    receiveVersionId: job.receiveVersionId ?? undefined,
    outputFormats: outputFormats.length ? outputFormats : undefined,
    outputUploadUrl: (outputFormats.length || isReceive) ? outputBaseUrl : undefined,
    options,
  };

  try {
    agent.socket.send(JSON.stringify(envelope('assign', assign, randomUUID())));
    agent.slotsBusy += 1;
  } catch (err) {
    log.warn({ err, agentSessionId: agent.sessionId }, 'agent ws send failed; will requeue');
    return { dispatched: false, reason: 'agent send failed' };
  }

  await db
    .update(jobs)
    .set({
      status: 'dispatched',
      nodeName: agent.nodeName,
      agentSessionId: agent.sessionId,
      currentStage: 'dispatched',
      updatedAt: new Date(),
    })
    .where(eq(jobs.id, job.id));

  broadcastJobUpdate(job.id, { status: 'dispatched', nodeName: agent.nodeName });

  log.info({ jobId: job.id, nodeName: agent.nodeName, sessionId: agent.sessionId }, 'job dispatched');
  return { dispatched: true, agentSessionId: agent.sessionId, nodeName: agent.nodeName };
}

/* -------------------------------------------------------------------------- */
/* Layer descendant expansion                                                  */
/* -------------------------------------------------------------------------- */

interface LayerNode {
  name: string;
  fullPath?: string;
  children?: LayerNode[];
}

/**
 * Given a flat list of selected `FullPath` strings and a nested layer tree,
 * return the full set of selected layers PLUS every descendant. Order is
 * preserved (selected first, then descendants in tree order).
 *
 * The agent's `LayerMode.ByLayer` filter does an exact `FullPath` match
 * (see RhinoSendPipeline.GetFilteredObjects), so the server is responsible
 * for spelling out every layer name the user actually wants included.
 */
export function expandLayerSelection(selected: string[], tree: LayerNode[]): string[] {
  const selectedSet = new Set(selected);
  const out = new Set<string>(selected);

  function walk(node: LayerNode, parentSelected: boolean) {
    const fp = node.fullPath ?? node.name;
    const isSelected = parentSelected || selectedSet.has(fp);
    if (isSelected) out.add(fp);
    if (node.children?.length) {
      for (const child of node.children) walk(child, isSelected);
    }
  }
  for (const root of tree) walk(root, false);
  return [...out];
}
