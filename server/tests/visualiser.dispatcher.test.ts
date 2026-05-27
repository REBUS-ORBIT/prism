/**
 * `tryDispatchVisualisation` selection logic — exercises eligibility
 * filtering (`can_visualise`, `is_enabled`, slot cap), least-loaded
 * ordering, atomic reservation race outcomes, and rollback on agent
 * send failure.
 *
 * `db` is mocked at the module boundary with a thin fixture that:
 *   - returns a configurable visualiser_runs row from `db.query`
 *   - returns a configurable workstations[] from `db.select().from()`
 *   - records `db.update(...).set(...).where(...).returning()` calls so
 *     tests can assert reservation atomicity + ordering
 *
 * `sessionRegistry` and `getSetting` are mocked likewise so the suite
 * runs purely in-process — no Postgres / Redis / WS gateway required.
 */
import { describe, expect, it, vi, beforeEach } from 'vitest';

// -----------------------------------------------------------------------------
// Mocks
// -----------------------------------------------------------------------------
// Test-controlled state. Each test reseeds these before the dispatcher runs.
const state = {
  run: null as null | Record<string, unknown>,
  workstations: [] as Record<string, unknown>[],
  agents: [] as {
    sessionId: string; machineId: string; workstationId: string; nodeName: string;
    socket: { send: ReturnType<typeof vi.fn>; readyState: number };
    hello: { slots: number };
    slotsBusy: number;
    connectedAt: Date;
  }[],
  /** Returned in order from successive `.returning()` calls on UPDATE workstations. */
  reservationOutcomes: [] as { id: string; currentVisualiserLoad: number }[][],
  /** Captured UPDATE workstations payloads. */
  workstationUpdates: [] as { set: unknown; where: unknown; returning: unknown }[],
  /** Captured UPDATE visualiser_runs payloads. */
  runUpdates: [] as { set: unknown; where: unknown }[],
  settings: { orbit_server_url: 'https://orbit.example.com', orbit_token: 't' } as Record<string, string | undefined>,
};

vi.mock('../src/db/client.js', () => {
  // Drizzle's API is fluent; we mimic only the surface the dispatcher uses.
  const db = {
    query: {
      visualiserRuns: {
        findFirst: vi.fn(async () => state.run),
      },
    },
    select: () => ({ from: async (_table: unknown) => state.workstations }),
    update: (table: { tableName?: string } | unknown) => {
      // Drizzle exposes a Symbol-tagged table identity; we sniff via a
      // hack: the dispatcher imports `workstations` and `visualiserRuns`
      // from the schema module which we also mock below. The mocked
      // table objects carry a `_kind` tag we can branch on.
      const kind = (table as { _kind?: string })._kind ?? 'unknown';
      return {
        set: (setPayload: unknown) => ({
          where: (wherePayload: unknown) => {
            if (kind === 'workstations') {
              // Capture the update at where() time so callers that omit
              // `.returning()` (like releaseVisualiserSlot) are still
              // recorded. `.returning()` overlays the captured row with
              // the next pre-seeded reservation outcome.
              const entry = { set: setPayload, where: wherePayload, returning: [] as unknown };
              state.workstationUpdates.push(entry);
              const returning = (_cols: unknown) => {
                const result = state.reservationOutcomes.shift() ?? [];
                entry.returning = result;
                return Promise.resolve(result);
              };
              const thenable: Promise<unknown> & { returning?: typeof returning } =
                Promise.resolve(undefined) as Promise<unknown> & { returning?: typeof returning };
              thenable.returning = returning;
              return thenable;
            }
            // visualiserRuns update — no returning() needed.
            state.runUpdates.push({ set: setPayload, where: wherePayload });
            return Promise.resolve(undefined);
          },
        }),
      };
    },
  };
  return { db };
});

vi.mock('../src/db/schema.js', () => {
  const tag = (kind: string) => new Proxy({ _kind: kind } as Record<string, unknown>, {
    get: (target, prop) => {
      if (prop === '_kind') return target._kind;
      // Every column access returns a sentinel string; the dispatcher only
      // uses these for drizzle's where/eq builders which we treat opaquely.
      return Symbol(`${kind}.${String(prop)}`);
    },
  });
  return {
    workstations: tag('workstations'),
    visualiserRuns: tag('visualiserRuns'),
    jobs: tag('jobs'),
  };
});

vi.mock('drizzle-orm', () => ({
  // The dispatcher only invokes these to build the where clauses we
  // capture opaquely — they don't need to do anything meaningful.
  eq:  (...a: unknown[]) => ({ _op: 'eq',  args: a }),
  and: (...a: unknown[]) => ({ _op: 'and', args: a }),
  sql: (strings: TemplateStringsArray, ...values: unknown[]) => ({ _op: 'sql', strings, values }),
}));

vi.mock('../src/db/settings.js', () => ({
  getSetting: vi.fn(async (k: string) => state.settings[k]),
}));

vi.mock('../src/ws/sessionRegistry.js', () => ({
  sessionRegistry: {
    allAgents: () => state.agents,
  },
}));

vi.mock('../src/ws/adminProtocol.js', () => ({
  broadcastJobUpdate: vi.fn(),
  broadcastWorkstationUpdate: vi.fn(),
}));

vi.mock('../src/api/internal.js', () => ({
  issueDownloadToken: vi.fn(async () => 'fake-token'),
}));

// -----------------------------------------------------------------------------
// SUT (imported after mocks so the mocks win)
// -----------------------------------------------------------------------------
import { tryDispatchVisualisation, releaseVisualiserSlot } from '../src/jobs/dispatcher.js';

function makeAgent(opts: Partial<{ sessionId: string; machineId: string; workstationId: string; slots: number; connectedAtMs: number }> = {}) {
  return {
    sessionId: opts.sessionId ?? 'sess-1',
    machineId: opts.machineId ?? 'mach-1',
    workstationId: opts.workstationId ?? 'ws-1',
    nodeName: 'node-1',
    socket: { send: vi.fn(), readyState: 1 },
    hello: { slots: opts.slots ?? 2 },
    slotsBusy: 0,
    connectedAt: new Date(opts.connectedAtMs ?? 1_700_000_000_000),
  };
}

function makeWs(opts: Partial<{ id: string; machineId: string; canVisualise: boolean; isEnabled: boolean; currentVisualiserLoad: number }> = {}) {
  return {
    id: opts.id ?? 'ws-1',
    machineId: opts.machineId ?? 'mach-1',
    isEnabled: opts.isEnabled ?? true,
    canVisualise: opts.canVisualise ?? true,
    canConvert: false, canLayer: false, canReceive: false,
    currentVisualiserLoad: opts.currentVisualiserLoad ?? 0,
    supportedFormats: [],
  };
}

function makeRun(overrides: Record<string, unknown> = {}) {
  return {
    id: 'run-1',
    status: 'queued',
    orbitTarget: 'prod',
    projectId: 'p-1',
    modelId:   'm-1',
    versionId: null,
    templateTag: null,
    signallingUrl: null,
    ttlSeconds: null,
    ...overrides,
  };
}

const log = { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() } as unknown as Parameters<typeof tryDispatchVisualisation>[1];

beforeEach(() => {
  state.run = makeRun();
  state.workstations = [];
  state.agents = [];
  state.reservationOutcomes = [];
  state.workstationUpdates = [];
  state.runUpdates = [];
  state.settings = { orbit_server_url: 'https://orbit.example.com', orbit_token: 't' };
  vi.clearAllMocks();
});

describe('tryDispatchVisualisation', () => {
  it('returns no_workstation_available when no agent is backed by a visualiser-capable workstation', async () => {
    state.workstations = [makeWs({ canVisualise: false })];
    state.agents = [makeAgent()];
    const out = await tryDispatchVisualisation('run-1', log);
    expect(out).toMatchObject({ dispatched: false, error: 'no_workstation_available' });
  });

  it('returns all_workstations_busy when every capable workstation is at capacity', async () => {
    state.workstations = [makeWs({ id: 'ws-1', machineId: 'mach-1', currentVisualiserLoad: 2 })];
    state.agents = [makeAgent({ slots: 2 })];
    const out = await tryDispatchVisualisation('run-1', log);
    expect(out).toMatchObject({ dispatched: false, error: 'all_workstations_busy' });
  });

  it('picks the least-loaded capable workstation and atomically reserves a slot', async () => {
    state.workstations = [
      makeWs({ id: 'ws-busy', machineId: 'mach-busy', currentVisualiserLoad: 1 }),
      makeWs({ id: 'ws-idle', machineId: 'mach-idle', currentVisualiserLoad: 0 }),
    ];
    state.agents = [
      makeAgent({ sessionId: 'sess-busy', machineId: 'mach-busy', workstationId: 'ws-busy', slots: 2, connectedAtMs: 100 }),
      makeAgent({ sessionId: 'sess-idle', machineId: 'mach-idle', workstationId: 'ws-idle', slots: 2, connectedAtMs: 200 }),
    ];
    // Reservation against the idle row succeeds first try.
    state.reservationOutcomes = [[{ id: 'ws-idle', currentVisualiserLoad: 1 }]];

    const out = await tryDispatchVisualisation('run-1', log);
    expect(out.dispatched).toBe(true);
    if (out.dispatched) {
      expect(out.workstationId).toBe('ws-idle');
      expect(out.agentSessionId).toBe('sess-idle');
    }
    // The single UPDATE we executed should have been against the workstations table.
    expect(state.workstationUpdates).toHaveLength(1);
    // The visualiser_runs row should have been transitioned to 'importing'.
    expect(state.runUpdates).toHaveLength(1);
    expect((state.runUpdates[0]!.set as { status: string }).status).toBe('importing');
    // And the agent's WS should have received exactly one envelope.
    expect(state.agents[1]!.socket.send).toHaveBeenCalledTimes(1);
    const sent = JSON.parse(state.agents[1]!.socket.send.mock.calls[0]![0]);
    expect(sent.type).toBe('startVisualisation');
    expect(sent.data.runId).toBe('run-1');
  });

  it('rolls forward to the next candidate when the first reservation loses the race (returns zero rows)', async () => {
    state.workstations = [
      makeWs({ id: 'ws-a', machineId: 'mach-a', currentVisualiserLoad: 0 }),
      makeWs({ id: 'ws-b', machineId: 'mach-b', currentVisualiserLoad: 0 }),
    ];
    state.agents = [
      makeAgent({ sessionId: 'sess-a', machineId: 'mach-a', workstationId: 'ws-a', slots: 1, connectedAtMs: 100 }),
      makeAgent({ sessionId: 'sess-b', machineId: 'mach-b', workstationId: 'ws-b', slots: 1, connectedAtMs: 200 }),
    ];
    // First reservation comes back empty (sibling dispatcher won the race); second succeeds.
    state.reservationOutcomes = [
      [],
      [{ id: 'ws-b', currentVisualiserLoad: 1 }],
    ];
    const out = await tryDispatchVisualisation('run-1', log);
    expect(out.dispatched).toBe(true);
    if (out.dispatched) expect(out.workstationId).toBe('ws-b');
    expect(state.workstationUpdates).toHaveLength(2);
  });

  it('returns all_workstations_busy when every reservation race is lost', async () => {
    state.workstations = [makeWs({ id: 'ws-1', machineId: 'mach-1', currentVisualiserLoad: 0 })];
    state.agents = [makeAgent({ slots: 1 })];
    state.reservationOutcomes = [[]];
    const out = await tryDispatchVisualisation('run-1', log);
    expect(out).toMatchObject({ dispatched: false, error: 'all_workstations_busy' });
  });

  it('rolls back the reservation when the agent ws send throws', async () => {
    state.workstations = [makeWs({ id: 'ws-1', machineId: 'mach-1', currentVisualiserLoad: 0 })];
    const agent = makeAgent({ slots: 1 });
    agent.socket.send = vi.fn(() => { throw new Error('broken pipe'); });
    state.agents = [agent];
    // First UPDATE = reservation (returns one row). Second UPDATE = rollback (no returning).
    state.reservationOutcomes = [[{ id: 'ws-1', currentVisualiserLoad: 1 }]];

    const out = await tryDispatchVisualisation('run-1', log);
    expect(out).toMatchObject({ dispatched: false, error: 'agent_send_failed' });
    // Two workstation UPDATEs: one to reserve, one to release.
    expect(state.workstationUpdates).toHaveLength(2);
  });

  it('returns misconfigured when ORBIT URL is unset for the run target', async () => {
    state.workstations = [makeWs({ id: 'ws-1', machineId: 'mach-1' })];
    state.agents = [makeAgent({ slots: 1 })];
    state.reservationOutcomes = [[{ id: 'ws-1', currentVisualiserLoad: 1 }]];
    state.settings = {}; // no ORBIT URL

    const out = await tryDispatchVisualisation('run-1', log);
    expect(out).toMatchObject({ dispatched: false, error: 'misconfigured' });
    // Reservation must have been rolled back.
    expect(state.workstationUpdates.length).toBeGreaterThanOrEqual(2);
  });

  it('refuses to dispatch a non-queued run (defensive idempotency)', async () => {
    state.run = makeRun({ status: 'importing' });
    state.workstations = [makeWs()];
    state.agents = [makeAgent()];
    const out = await tryDispatchVisualisation('run-1', log);
    expect(out).toMatchObject({ dispatched: false, error: 'invalid_state' });
  });

  it('refuses to dispatch when the run row does not exist', async () => {
    state.run = null;
    const out = await tryDispatchVisualisation('missing', log);
    expect(out).toMatchObject({ dispatched: false, error: 'invalid_state' });
  });
});

describe('releaseVisualiserSlot', () => {
  it('issues a workstations UPDATE clamped at zero', async () => {
    await releaseVisualiserSlot('ws-1');
    expect(state.workstationUpdates).toHaveLength(1);
    const upd = state.workstationUpdates[0]!;
    // The set payload is the raw sql template — assert it's a GREATEST clamp,
    // not a bare decrement that could go negative.
    const setStr = JSON.stringify((upd.set as { currentVisualiserLoad: { strings: TemplateStringsArray } }).currentVisualiserLoad.strings);
    expect(setStr).toMatch(/GREATEST/);
  });
});
