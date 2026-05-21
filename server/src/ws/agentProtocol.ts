/**
 * Typed agent WS handler.
 *
 * One handler instance per agent connection. Lifecycle:
 *   1. open  -> wait for `hello`
 *   2. hello -> upsert workstations + agent_sessions row, send `welcome`
 *   3. heartbeat -> bump session, update slotsBusy
 *   4. progress/log/complete/fail/layers -> update job row, fan to admin
 *   5. close -> remove session row + registry entry
 */
import { randomUUID } from 'node:crypto';
import type { WebSocket } from 'ws';
import type { FastifyBaseLogger } from 'fastify';
import { and, eq } from 'drizzle-orm';
import { db } from '../db/client.js';
import { agentSessions, jobLogs, jobs, workstations } from '../db/schema.js';
import { sessionRegistry, type AgentConn } from './sessionRegistry.js';
import {
  envelope, PROTOCOL_VERSION,
  type AgentToServerMsg, type HelloData, type WelcomeData,
} from '../../../shared/contracts/agent-protocol.js';
import { broadcastJobUpdate, broadcastWorkstationUpdate } from './adminProtocol.js';
import { dispatchJobEvent } from '../webhooks/dispatcher.js';

const HEARTBEAT_SECONDS = 15;

export async function handleAgentSocket(socket: WebSocket, remoteAddr: string | undefined, log: FastifyBaseLogger): Promise<void> {
  let conn: AgentConn | null = null;
  let helloProcessed = false;
  const childLog = log.child({ ws: 'agent' });

  // Send a JSON-level server_ping every 30 s. The Websocket.Client library on
  // the agent side only resets its ReconnectTimeout (60 s) on application-
  // layer text frames — protocol-level WS pings are invisible to it. Without
  // this the agent re-connects every ~61 s even when the connection is healthy.
  const pingFrame = JSON.stringify(envelope('server_ping', {}));
  const pingInterval = setInterval(() => {
    if (socket.readyState === socket.OPEN) socket.send(pingFrame);
  }, 30_000);

  socket.on('message', (raw) => {
    let msg: AgentToServerMsg;
    try {
      msg = JSON.parse(raw.toString());
    } catch (err) {
      childLog.warn({ err }, 'agent sent non-JSON; closing');
      socket.close(1003, 'invalid json');
      return;
    }
    if (msg.v !== PROTOCOL_VERSION) {
      childLog.warn({ got: msg.v }, 'protocol version mismatch; closing');
      socket.close(1002, 'protocol version mismatch');
      return;
    }
    void handleMessage(msg).catch((err) => {
      childLog.error({ err, type: msg.type }, 'agent handler failed');
    });
  });

  socket.on('close', async (code, reason) => {
    clearInterval(pingInterval);
    if (conn) {
      childLog.info({ sessionId: conn.sessionId, nodeName: conn.nodeName, code, reason: reason.toString() }, 'agent ws closed');
      sessionRegistry.removeAgent(conn.sessionId);
      try {
        await db.delete(agentSessions).where(eq(agentSessions.id, conn.sessionId));
        broadcastWorkstationUpdate({ id: conn.workstationId, online: false });
      } catch (err) {
        childLog.warn({ err }, 'failed to delete agent_sessions row on close');
      }
    }
  });

  socket.on('error', (err) => {
    childLog.warn({ err }, 'agent ws error');
  });

  async function handleMessage(msg: AgentToServerMsg) {
    switch (msg.type) {
      case 'hello':
        await onHello(msg.data);
        return;
      case 'heartbeat':
        if (!conn) return;
        conn.lastHeartbeat = new Date();
        conn.slotsBusy = msg.data.slotsBusy;
        await db
          .update(agentSessions)
          .set({ lastHeartbeat: conn.lastHeartbeat, slotsBusy: conn.slotsBusy })
          .where(eq(agentSessions.id, conn.sessionId));
        return;
      case 'ack':
        if (!conn) return;
        childLog.debug({ jobId: msg.data.jobId, accepted: msg.data.accepted }, 'agent ack');
        return;
      case 'progress':
        if (!conn) return;
        await db
          .update(jobs)
          .set({
            status: 'processing',
            currentStage: msg.data.stage,
            progressPercent: msg.data.percent ?? null,
            lastMessage: msg.data.message ?? null,
            updatedAt: new Date(),
          })
          .where(eq(jobs.id, msg.data.jobId));
        broadcastJobUpdate(msg.data.jobId, {
          status: 'processing',
          currentStage: msg.data.stage,
          progressPercent: msg.data.percent,
          lastMessage: msg.data.message,
        });
        return;
      case 'log':
        if (!conn) return;
        await db.insert(jobLogs).values({
          jobId: msg.data.jobId,
          level: msg.data.level,
          source: 'agent',
          message: msg.data.message,
        });
        broadcastJobUpdate(msg.data.jobId, { logLine: { ts: new Date().toISOString(), ...msg.data } });
        return;
      case 'complete':
        if (!conn) return;
        await db
          .update(jobs)
          .set({
            status: 'complete',
            resultUrl: msg.data.versionUrl ?? null,
            rootObjectId: msg.data.rootObjectId ?? null,
            versionId: msg.data.versionId ?? null,
            outputs: msg.data.outputs ?? {},
            currentStage: 'complete',
            progressPercent: 100,
            completedAt: new Date(),
            updatedAt: new Date(),
          })
          .where(eq(jobs.id, msg.data.jobId));
        broadcastJobUpdate(msg.data.jobId, { status: 'complete', resultUrl: msg.data.versionUrl, outputs: msg.data.outputs });
        void dispatchJobEvent('job.complete', msg.data.jobId).catch((err) => childLog.warn({ err }, 'webhook dispatch failed'));
        return;
      case 'fail':
        if (!conn) return;
        await db
          .update(jobs)
          .set({
            status: 'failed',
            error: msg.data.error,
            currentStage: 'failed',
            updatedAt: new Date(),
            completedAt: new Date(),
          })
          .where(eq(jobs.id, msg.data.jobId));
        broadcastJobUpdate(msg.data.jobId, { status: 'failed', error: msg.data.error });
        void dispatchJobEvent('job.failed', msg.data.jobId).catch((err) => childLog.warn({ err }, 'webhook dispatch failed'));
        return;
      case 'layers':
        if (!conn) return;
        childLog.info({ jobId: msg.data.jobId, count: msg.data.layers.length }, 'received layer tree');
        // Phase 6 wires this into a per-job layer cache and the convert UI.
        return;
    }
  }

  async function onHello(hello: HelloData) {
    if (helloProcessed) {
      childLog.warn({ machineId: hello.machineId }, 'duplicate hello on same socket; ignoring');
      return;
    }
    helloProcessed = true;
    childLog.info({ machineId: hello.machineId, nodeName: hello.nodeName, slots: hello.slots }, 'agent hello');

    // Upsert workstation row by machineId.
    let workstation = (await db
      .select()
      .from(workstations)
      .where(eq(workstations.machineId, hello.machineId))
      .limit(1))[0];

    if (!workstation) {
      const inserted = await db
        .insert(workstations)
        .values({
          machineId: hello.machineId,
          nodeName: hello.nodeName,
          supportedFormats: hello.formats,
          slotsTotal: hello.slots,
          agentVersion: hello.agentVersion,
          rhinoVersion: hello.rhinoVersion ?? null,
          canConvert: hello.roles.includes('conversion'),
          canLayer:   hello.roles.includes('layering'),
          canReceive: hello.roles.includes('receive'),
          lastSeenAt: new Date(),
        })
        .returning();
      workstation = inserted[0]!;
      childLog.info({ workstationId: workstation.id }, 'registered new workstation');
    } else {
      await db
        .update(workstations)
        .set({
          nodeName: hello.nodeName,
          supportedFormats: hello.formats,
          slotsTotal: hello.slots,
          agentVersion: hello.agentVersion,
          rhinoVersion: hello.rhinoVersion ?? null,
          canConvert: hello.roles.includes('conversion'),
          canLayer:   hello.roles.includes('layering'),
          canReceive: hello.roles.includes('receive'),
          lastSeenAt: new Date(),
        })
        .where(eq(workstations.id, workstation.id));
    }

    if (!workstation.isEnabled) {
      socket.send(JSON.stringify(envelope('cancel', { jobId: '00000000-0000-0000-0000-000000000000', reason: 'workstation disabled by admin' })));
      socket.close(1008, 'workstation disabled');
      return;
    }

    // Insert agent_sessions row (id = sessionId we'll hand back in welcome)
    const inserted = await db
      .insert(agentSessions)
      .values({
        workstationId: workstation.id,
        remoteAddr: remoteAddr ?? null,
        slotsBusy: 0,
      })
      .returning();
    const session = inserted[0]!;

    conn = {
      sessionId: session.id,
      workstationId: workstation.id,
      machineId: hello.machineId,
      nodeName: hello.nodeName,
      socket,
      hello,
      slotsBusy: 0,
      connectedAt: session.connectedAt!,
      lastHeartbeat: new Date(),
      remoteAddr,
    };
    sessionRegistry.addAgent(conn);

    const welcome: WelcomeData = {
      sessionId: session.id,
      serverTime: new Date().toISOString(),
      heartbeatSeconds: HEARTBEAT_SECONDS,
    };
    socket.send(JSON.stringify(envelope('welcome', welcome, randomUUID())));

    broadcastWorkstationUpdate({
      id: workstation.id,
      nodeName: workstation.nodeName,
      online: true,
      slotsTotal: workstation.slotsTotal,
      slotsBusy: 0,
    });
  }
}
