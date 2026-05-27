/**
 * Pixel Streaming signalling proxy.
 *
 * Surface:
 *   /ws/visualiser/:runId/signalling?token=<jwt>
 *
 * Auth: short-lived HS256 JWT minted by
 *   POST /api/visualiser/streams/:runId/signalling-token
 * (see ../visualiser/signallingToken.ts). Rejected with 401 on any
 * sig/exp/runId mismatch.
 *
 * Pipeline:
 *   Browser  ⇄  PRISM server  ⇄  Agent WS  ⇄  local Cirrus on workstation
 *
 * PRISM does not parse the Pixel Streaming WebRTC sub-protocol. Every
 * browser frame is wrapped into a `signallingFrame` envelope (with
 * either `payload` for text or `payloadB64` for binary) and forwarded
 * to the agent. The agent unwraps the envelope and writes to its
 * local Cirrus WS; the reverse direction does the same. See
 * `PRISM/agent/src/PRISM.Agent/Ws/AgentMessageDispatcher.cs`.
 *
 * Lifecycle:
 *   - On WS open we authenticate, then look up the run row.
 *   - We refuse to connect unless `status='streaming'` (no point
 *     attempting WebRTC negotiation against an importing run).
 *   - We register the browser socket in the proxy registry, keyed by
 *     runId. Concurrent browser tabs on the same runId are allowed
 *     and each receives a fan-out of inbound agent frames.
 *   - On close we drop the socket; if it was the last for `runId`
 *     the registry entry is reaped.
 *
 * The companion `agentProtocol.ts` calls
 * `signallingProxyRegistry.forwardAgentToBrowser(frame)` for every
 * inbound `signallingFrame` from the agent.
 */
import type { FastifyPluginAsync } from 'fastify';
import { eq } from 'drizzle-orm';
import { db } from '../db/client.js';
import { visualiserRuns } from '../db/schema.js';
import { sendSignallingFrameToAgent } from './agentProtocol.js';
import { verifySignallingToken } from '../visualiser/signallingToken.js';
import { signallingProxyRegistry, type BrowserConn } from './signallingProxyRegistry.js';
import type { SignallingFrameData } from '../../../shared/contracts/agent-protocol.js';

const plugin: FastifyPluginAsync = async (app) => {
  app.get<{ Params: { runId: string }; Querystring: { token?: string } }>(
    '/ws/visualiser/:runId/signalling',
    { websocket: true },
    async (socket, req) => {
      const childLog = req.log.child({ ws: 'signalling-proxy', runId: req.params.runId });
      const token = req.query.token;
      if (typeof token !== 'string' || token.length === 0) {
        childLog.warn('missing signalling token');
        socket.close(4401, 'missing token');
        return;
      }
      const verified = verifySignallingToken(token, { expectedRunId: req.params.runId });
      if (!verified.ok) {
        childLog.warn({ error: verified.error }, 'signalling token rejected');
        socket.close(4401, verified.error);
        return;
      }

      const row = await db.query.visualiserRuns.findFirst({
        where: eq(visualiserRuns.id, req.params.runId),
      });
      if (!row) {
        socket.close(4404, 'run not found');
        return;
      }
      if (row.status !== 'streaming') {
        socket.close(4409, `run is ${row.status}`);
        return;
      }
      if (!row.agentSessionId) {
        socket.close(4503, 'no agent assigned to run');
        return;
      }

      const conn: BrowserConn = {
        socket,
        agentSessionId: row.agentSessionId,
        runId: row.id,
      };
      signallingProxyRegistry.add(conn);
      childLog.info({ agentSessionId: conn.agentSessionId }, 'browser signalling ws connected');

      socket.on('message', (data, isBinary) => {
        const frame: SignallingFrameData = isBinary
          ? { runId: conn.runId, payloadB64: (data as Buffer).toString('base64') }
          : { runId: conn.runId, payload: data.toString() };
        const ok = sendSignallingFrameToAgent(conn.agentSessionId, frame);
        if (!ok) {
          childLog.warn('agent send failed; closing browser socket');
          try { socket.close(1011, 'agent unreachable'); } catch { /* ignore */ }
        }
      });

      socket.on('close', (code, reason) => {
        signallingProxyRegistry.remove(conn);
        childLog.info({ code, reason: reason.toString() }, 'browser signalling ws closed');
      });

      socket.on('error', (err) => {
        childLog.warn({ err }, 'browser signalling ws error');
      });
    },
  );
};

export default plugin;
