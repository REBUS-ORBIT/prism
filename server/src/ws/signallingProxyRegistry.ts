/**
 * In-process registry of browser ⇄ PRISM signalling WS connections.
 *
 * Extracted from `signallingProxy.ts` so `agentProtocol.ts` can import
 * the registry without creating a circular dependency (the proxy
 * plugin imports `sendSignallingFrameToAgent` from agentProtocol; the
 * agent protocol handler imports the registry to fan inbound frames
 * back to browsers).
 *
 * One registry entry per (runId, browser socket). A run may have
 * multiple concurrent browser viewers — see signallingProxy.ts for
 * the multi-viewer rationale.
 */
import type { WebSocket } from 'ws';
import type { SignallingFrameData } from '../../../shared/contracts/agent-protocol.js';

export interface BrowserConn {
  socket: WebSocket;
  agentSessionId: string;
  runId: string;
}

class SignallingProxyRegistry {
  private byRun = new Map<string, Set<BrowserConn>>();

  add(conn: BrowserConn): void {
    const set = this.byRun.get(conn.runId) ?? new Set<BrowserConn>();
    set.add(conn);
    this.byRun.set(conn.runId, set);
  }

  remove(conn: BrowserConn): void {
    const set = this.byRun.get(conn.runId);
    if (!set) return;
    set.delete(conn);
    if (set.size === 0) this.byRun.delete(conn.runId);
  }

  /**
   * Fan an agent-originated frame out to every browser tab connected
   * to the same runId. Pixel Streaming 2 supports multi-viewer in
   * principle (separate WebRTC sessions per peer), but for v1 PRISM
   * expects a single browser per run — the fan-out is defensive.
   */
  forwardAgentToBrowser(frame: SignallingFrameData): void {
    const set = this.byRun.get(frame.runId);
    if (!set || set.size === 0) return;
    const text = typeof frame.payload === 'string' ? frame.payload : null;
    const bin  = typeof frame.payloadB64 === 'string' ? Buffer.from(frame.payloadB64, 'base64') : null;
    for (const conn of set) {
      if (conn.socket.readyState !== conn.socket.OPEN) continue;
      try {
        if (text != null) conn.socket.send(text);
        else if (bin != null) conn.socket.send(bin);
      } catch {
        /* socket presumably closing — `close` cleanup will reap it */
      }
    }
  }

  /** Close every browser proxy connection for `runId` with the given code/reason. */
  closeRun(runId: string, code: number, reason: string): void {
    const set = this.byRun.get(runId);
    if (!set) return;
    for (const conn of set) {
      try { conn.socket.close(code, reason); } catch { /* ignore */ }
    }
    this.byRun.delete(runId);
  }

  size(): number { return this.byRun.size; }
}

export const signallingProxyRegistry = new SignallingProxyRegistry();
