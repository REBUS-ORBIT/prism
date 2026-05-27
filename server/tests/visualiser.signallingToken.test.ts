/**
 * HS256 JWT issue/verify cycle for `/ws/visualiser/:runId/signalling`.
 *
 * The helpers are pure — no DB, no HTTP — so tests inject `now` and
 * `env` and assert on success / failure modes directly.
 */
import { describe, expect, it } from 'vitest';
import { issueSignallingToken, verifySignallingToken } from '../src/visualiser/signallingToken.js';

const FIXED_NOW = new Date('2026-05-27T18:00:00.000Z');
const SECRET = 'unit-test-jwt-secret';
const ENV  = { JWT_SIGNALLING_SECRET: SECRET };
const ENV2 = { JWT_SIGNALLING_SECRET: 'a-different-secret' };

describe('issueSignallingToken', () => {
  it('refuses to mint when JWT_SIGNALLING_SECRET is unset', () => {
    expect(() => issueSignallingToken({
      runId: '5b9c1d4f',
      env: {},
      now: () => FIXED_NOW,
    })).toThrow(/JWT_SIGNALLING_SECRET/);
  });

  it('produces a three-part token with the requested TTL', () => {
    const { token, exp } = issueSignallingToken({
      runId: '5b9c1d4f',
      ttlSeconds: 60,
      env: ENV,
      now: () => FIXED_NOW,
    });
    expect(token.split('.').length).toBe(3);
    expect(exp).toBe(Math.floor(FIXED_NOW.getTime() / 1000) + 60);
  });

  it('rejects non-positive TTLs', () => {
    expect(() => issueSignallingToken({
      runId: '5b9c1d4f', ttlSeconds: 0, env: ENV, now: () => FIXED_NOW,
    })).toThrow(/positive integer/);
  });
});

describe('verifySignallingToken', () => {
  it('accepts a freshly-issued token with matching runId', () => {
    const { token } = issueSignallingToken({
      runId: '5b9c1d4f', ttlSeconds: 60, env: ENV, now: () => FIXED_NOW,
    });
    const v = verifySignallingToken(token, {
      expectedRunId: '5b9c1d4f', env: ENV, now: () => FIXED_NOW,
    });
    expect(v.ok).toBe(true);
    if (v.ok) {
      expect(v.payload.runId).toBe('5b9c1d4f');
      expect(v.payload.exp).toBeGreaterThan(v.payload.iat);
    }
  });

  it('rejects a token signed with a different secret', () => {
    const { token } = issueSignallingToken({
      runId: '5b9c1d4f', env: ENV, now: () => FIXED_NOW,
    });
    const v = verifySignallingToken(token, {
      expectedRunId: '5b9c1d4f', env: ENV2, now: () => FIXED_NOW,
    });
    expect(v.ok).toBe(false);
    if (!v.ok) expect(v.error).toMatch(/bad signature/);
  });

  it('rejects a token for a different runId (replay protection)', () => {
    const { token } = issueSignallingToken({
      runId: 'run-A', env: ENV, now: () => FIXED_NOW,
    });
    const v = verifySignallingToken(token, {
      expectedRunId: 'run-B', env: ENV, now: () => FIXED_NOW,
    });
    expect(v.ok).toBe(false);
    if (!v.ok) expect(v.error).toMatch(/runId mismatch/);
  });

  it('rejects an expired token', () => {
    const { token } = issueSignallingToken({
      runId: 'r', ttlSeconds: 60, env: ENV, now: () => FIXED_NOW,
    });
    const later = new Date(FIXED_NOW.getTime() + 120 * 1000);
    const v = verifySignallingToken(token, {
      expectedRunId: 'r', env: ENV, now: () => later,
    });
    expect(v.ok).toBe(false);
    if (!v.ok) expect(v.error).toMatch(/expired/);
  });

  it('rejects malformed tokens', () => {
    const v1 = verifySignallingToken('not-a-jwt', {
      expectedRunId: 'r', env: ENV, now: () => FIXED_NOW,
    });
    expect(v1.ok).toBe(false);

    const v2 = verifySignallingToken('a.b.c', {
      expectedRunId: 'r', env: ENV, now: () => FIXED_NOW,
    });
    expect(v2.ok).toBe(false);
  });

  it('refuses to verify when the secret is unset', () => {
    const { token } = issueSignallingToken({
      runId: 'r', env: ENV, now: () => FIXED_NOW,
    });
    const v = verifySignallingToken(token, {
      expectedRunId: 'r', env: {}, now: () => FIXED_NOW,
    });
    expect(v.ok).toBe(false);
    if (!v.ok) expect(v.error).toMatch(/JWT_SIGNALLING_SECRET/);
  });
});
