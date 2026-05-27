/**
 * In-memory Promise registry used to bridge the synchronous
 * `POST /api/visualiser/streams` request to the agent's async
 * `visualisationReady` / `visualisationFailed` reply.
 */
import { describe, expect, it, vi } from 'vitest';
import { visualiserRunRegistry } from '../src/visualiser/runRegistry.js';

describe('RunRegistry', () => {
  it('resolves a waiter when a ready event arrives', async () => {
    const runId = 'r-ready-' + Math.random().toString(36).slice(2, 8);
    const waiter = visualiserRunRegistry.waitFor(runId, 0);
    expect(visualiserRunRegistry.ready({ runId, signallingUrl: 'wss://x/y' })).toBe(true);
    await expect(waiter).resolves.toMatchObject({ runId, signallingUrl: 'wss://x/y' });
  });

  it('rejects a waiter when a failure event arrives', async () => {
    const runId = 'r-fail-' + Math.random().toString(36).slice(2, 8);
    const waiter = visualiserRunRegistry.waitFor(runId, 0);
    expect(visualiserRunRegistry.fail({ runId, code: 'agent_failed', message: 'no GPU' })).toBe(true);
    await expect(waiter).rejects.toMatchObject({ runId, code: 'agent_failed' });
  });

  it('fires a timeout when no event arrives in time', async () => {
    vi.useFakeTimers();
    try {
      const runId = 'r-timeout-' + Math.random().toString(36).slice(2, 8);
      const waiter = visualiserRunRegistry.waitFor(runId, 50);
      const settled = waiter.catch((e) => e);
      vi.advanceTimersByTime(60);
      const ev = await settled;
      expect(ev).toMatchObject({ runId, code: 'start_timeout' });
    } finally {
      vi.useRealTimers();
    }
  });

  it('supersedes an existing waiter when waitFor is called twice for the same runId', async () => {
    const runId = 'r-supersede-' + Math.random().toString(36).slice(2, 8);
    const first = visualiserRunRegistry.waitFor(runId, 0).catch((e) => e);
    const second = visualiserRunRegistry.waitFor(runId, 0);
    visualiserRunRegistry.ready({ runId, signallingUrl: 'wss://x/y' });
    await expect(first).resolves.toMatchObject({ code: 'superseded' });
    await expect(second).resolves.toMatchObject({ runId });
  });

  it('returns false from ready / fail when no waiter is registered (late arrival is safe)', () => {
    const runId = 'r-late-' + Math.random().toString(36).slice(2, 8);
    expect(visualiserRunRegistry.ready({ runId, signallingUrl: 'wss://x/y' })).toBe(false);
    expect(visualiserRunRegistry.fail({ runId, code: 'agent_failed', message: 'x' })).toBe(false);
  });

  it('abandon drops a waiter without rejecting it', async () => {
    const runId = 'r-abandon-' + Math.random().toString(36).slice(2, 8);
    const waiter = visualiserRunRegistry.waitFor(runId, 0);
    visualiserRunRegistry.abandon(runId);
    // Abandoning never fires the promise — confirm by racing with a quick resolve.
    const settled = await Promise.race([
      waiter.then(() => 'resolved' as const).catch(() => 'rejected' as const),
      new Promise<'pending'>((res) => setTimeout(() => res('pending'), 20)),
    ]);
    expect(settled).toBe('pending');
  });
});
