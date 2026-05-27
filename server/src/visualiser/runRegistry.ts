/**
 * Per-runId Promise registry for the synchronous
 * `POST /api/visualiser/streams` round-trip.
 *
 * The portal contract is synchronous — the caller blocks until the
 * agent either reports `visualisationReady` (success) or
 * `visualisationFailed` (terminal failure) or the configured timeout
 * fires. We persist the run row to Postgres up-front and send the
 * `startVisualisation` envelope over the agent WS, then `await`-ing
 * the registered Promise inside the route handler is what bridges
 * the request/response cycle to the async WS reply.
 *
 * Used by:
 *   - api/visualiser.ts  (creates the waiter, awaits resolution)
 *   - ws/agentProtocol.ts (resolves on `visualisationReady`,
 *                          rejects on `visualisationFailed`)
 *
 * The registry is intentionally in-memory — visualiser runs are
 * short-lived (max ~3 minutes from POST to ready) and we don't
 * survive a server restart with an in-flight request anyway. If a
 * restart drops the registry while a run is still importing, the
 * agent will eventually emit `visualisationReady` against a runId
 * with no waiter — the WS handler logs a debug line and the DB row
 * still transitions to `streaming` so a follow-up GET still works.
 */

export interface VisualiserReadyEvent {
  runId: string;
  signallingUrl: string;
  streamerId?: string;
  expiresAt?: string;
}

export interface VisualiserFailureEvent {
  runId: string;
  /** Machine-readable failure code (e.g. `agent_failed`, `start_timeout`). */
  code: string;
  /** Human-readable failure message surfaced to the caller. */
  message: string;
  stack?: string;
}

interface Waiter {
  resolve: (ev: VisualiserReadyEvent) => void;
  reject: (ev: VisualiserFailureEvent) => void;
  timer: NodeJS.Timeout | null;
}

class RunRegistry {
  private waiters = new Map<string, Waiter>();

  /**
   * Register a waiter for `runId`. Returns a Promise that the route
   * handler awaits; resolves on `visualisationReady`, rejects on
   * `visualisationFailed` or `cancel(runId, ...)`.
   *
   * Concurrent waiters for the same runId are not supported — the
   * second call rejects the previous waiter with `code:
   * 'superseded'`. (The route handler creates a runId via
   * `randomUUID()`, so collision is a defensive guard only.)
   */
  waitFor(runId: string, timeoutMs: number): Promise<VisualiserReadyEvent> {
    return new Promise<VisualiserReadyEvent>((resolve, reject) => {
      const existing = this.waiters.get(runId);
      if (existing) {
        if (existing.timer) clearTimeout(existing.timer);
        existing.reject({ runId, code: 'superseded', message: 'replaced by a newer wait registration' });
      }
      const timer = timeoutMs > 0
        ? setTimeout(() => {
            this.waiters.delete(runId);
            reject({ runId, code: 'start_timeout', message: `start exceeded ${timeoutMs}ms` });
          }, timeoutMs)
        : null;
      this.waiters.set(runId, { resolve, reject, timer });
    });
  }

  /**
   * Resolve the waiter for `runId` with a ready event. No-op when no
   * waiter is registered (the WS reply may arrive after the API
   * caller has timed out and walked away).
   */
  ready(ev: VisualiserReadyEvent): boolean {
    const w = this.waiters.get(ev.runId);
    if (!w) return false;
    this.waiters.delete(ev.runId);
    if (w.timer) clearTimeout(w.timer);
    w.resolve(ev);
    return true;
  }

  /** Reject the waiter for `runId`. */
  fail(ev: VisualiserFailureEvent): boolean {
    const w = this.waiters.get(ev.runId);
    if (!w) return false;
    this.waiters.delete(ev.runId);
    if (w.timer) clearTimeout(w.timer);
    w.reject(ev);
    return true;
  }

  /** Drop a waiter without resolving or rejecting (best-effort cleanup). */
  abandon(runId: string): void {
    const w = this.waiters.get(runId);
    if (!w) return;
    if (w.timer) clearTimeout(w.timer);
    this.waiters.delete(runId);
  }

  size(): number { return this.waiters.size; }
}

export const visualiserRunRegistry = new RunRegistry();
