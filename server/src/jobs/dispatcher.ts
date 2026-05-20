/**
 * Job dispatcher.
 *
 * Given a queued job, find an eligible agent slot and push an `assign`
 * message over its WS. Returns true if dispatched, false if no eligible
 * agent was available (the worker should requeue or hold).
 *
 * Eligibility:
 *   - workstation.is_enabled = true
 *   - workstation.can_convert = true (for convert jobs)
 *   - workstation.supported_formats includes job.format
 *   - agent has at least one free slot (slotsBusy < slotsTotal)
 */
import { randomUUID } from 'node:crypto';
import type { FastifyBaseLogger } from 'fastify';
import { eq } from 'drizzle-orm';
import { db } from '../db/client.js';
import { jobs, workstations } from '../db/schema.js';
import { getSetting } from '../db/settings.js';
import { envelope, type AssignData } from '../../../shared/contracts/agent-protocol.js';
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

  // Resolve workstations table once; live conn state from sessionRegistry.
  const wsRows = await db.select().from(workstations);
  const wsByMachine = new Map(wsRows.map((w) => [w.machineId, w]));

  const isReceive = job.jobType === 'receive';
  const eligible: AgentConn[] = [];
  for (const conn of sessionRegistry.allAgents()) {
    const w = wsByMachine.get(conn.machineId);
    if (!w || !w.isEnabled) continue;
    if (isReceive ? !w.canReceive : !w.canConvert) continue;
    // For receive jobs, supportedFormats gates the OUTPUT format (e.g. '3dm');
    // for convert it gates the INPUT format.
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
  if (!orbitServerUrl) {
    log.error({ orbitTarget: job.orbitTarget }, 'dispatch failed: no ORBIT server URL configured for target');
    return { dispatched: false, reason: `no ORBIT URL set for target=${job.orbitTarget}` };
  }

  const orbitToken =
    job.submittedBy?.startsWith('orbit:')
      // For Phase 3 we don't yet persist the bearer that was used at submit time.
      // The convert/async route in Phase 7 will stash it (encrypted) on the job row.
      ? (await getSetting('orbit_token')) ?? ''
      : (await getSetting('orbit_token')) ?? '';
  if (!orbitToken) {
    log.warn({ jobId }, 'no shared orbit_token; agent will get an empty bearer (Phase 7 fixes per-user tokens)');
  }

  // Convert jobs need a download URL for their input file. Receive jobs don't.
  let fileUrl: string | undefined;
  if (!isReceive) {
    const fileToken = await issueDownloadToken(job.id);
    fileUrl = `${PUBLIC_BASE_URL.replace(/\/$/, '')}/internal/files/${job.id}?token=${fileToken}`;
  }

  // Always provide an upload-back URL so the agent can deliver non-ORBIT outputs
  // (3DM, GLB, IFC, STEP, or the receive primary).
  const outputBaseUrl = `${PUBLIC_BASE_URL.replace(/\/$/, '')}/internal/outputs/${job.id}`;
  const outputFormats = (job.outputFormats as string[] | null) ?? [];

  const assign: AssignData = {
    jobId: job.id,
    jobType: isReceive ? 'receive' : 'convert',
    slot: agent.slotsBusy,
    format: job.format,
    fileUrl,
    fileName: job.fileName,
    orbitServerUrl,
    orbitToken,
    projectId: job.projectId,
    modelId: job.modelId,
    modelName: job.modelName ?? undefined,
    receiveVersionId: job.receiveVersionId ?? undefined,
    outputFormats: outputFormats.length ? outputFormats : undefined,
    outputUploadUrl: (outputFormats.length || isReceive) ? outputBaseUrl : undefined,
    options: (job.options as AssignData['options']) ?? undefined,
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
