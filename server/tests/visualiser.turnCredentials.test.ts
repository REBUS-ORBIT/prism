/**
 * RFC 7635 §3 long-term TURN credential generator tests.
 *
 * The generator is a pure function (apart from default Date.now / env),
 * so the suite injects fixed time + env to assert deterministic outputs.
 * No DB / network surface required.
 */
import { describe, expect, it } from 'vitest';
import { createHmac } from 'node:crypto';
import { generateTurnCredential } from '../src/visualiser/turnCredentials.js';

const FIXED_NOW = new Date('2026-05-27T18:00:00.000Z');
const FIXED_EPOCH = Math.floor(FIXED_NOW.getTime() / 1000); // 1748368800

describe('generateTurnCredential', () => {
  it('returns null sentinel when TURN_SECRET is unset', () => {
    const out = generateTurnCredential({
      runId: '5b9c1d4f-9d72-4a8c-8e64-7e22b5f2f01b',
      env: { /* deliberately empty */ },
      now: () => FIXED_NOW,
    });
    expect(out).toBeNull();
  });

  it('returns null sentinel when TURN_SECRET is empty string', () => {
    const out = generateTurnCredential({
      runId: '5b9c1d4f-9d72-4a8c-8e64-7e22b5f2f01b',
      env: { TURN_SECRET: '' },
      now: () => FIXED_NOW,
    });
    expect(out).toBeNull();
  });

  it('produces a deterministic RFC 7635 username + HMAC-SHA1 credential', () => {
    const runId = '5b9c1d4f-9d72-4a8c-8e64-7e22b5f2f01b';
    const out = generateTurnCredential({
      runId,
      ttlSeconds: 60,
      env: { TURN_SECRET: 'unit-test-secret' },
      now: () => FIXED_NOW,
    });
    expect(out).not.toBeNull();
    // username = `<exp>:<first-uuid-segment>`
    const expectedUsername = `${FIXED_EPOCH + 60}:5b9c1d4f`;
    expect(out!.username).toBe(expectedUsername);
    // credential = base64(HMAC-SHA1(secret, username))
    const expectedCred = createHmac('sha1', 'unit-test-secret').update(expectedUsername).digest('base64');
    expect(out!.credential).toBe(expectedCred);
    expect(out!.ttl).toBe(60);
  });

  it('honours TURN_REALM in the default URL list', () => {
    const out = generateTurnCredential({
      runId: 'abcdef12',
      env: { TURN_SECRET: 's', TURN_REALM: 'turn.example.com' },
      now: () => FIXED_NOW,
    });
    expect(out!.urls).toEqual([
      'turn:turn.example.com:3478',
      'turns:turn.example.com:5349',
    ]);
  });

  it('falls back to visualiser.rebus.industries when TURN_REALM is unset', () => {
    const out = generateTurnCredential({
      runId: 'abcdef12',
      env: { TURN_SECRET: 's' },
      now: () => FIXED_NOW,
    });
    expect(out!.urls).toEqual([
      'turn:visualiser.rebus.industries:3478',
      'turns:visualiser.rebus.industries:5349',
    ]);
  });

  it('TURN_URLS_OVERRIDE wins over the realm-derived defaults', () => {
    const out = generateTurnCredential({
      runId: 'abcdef12',
      env: {
        TURN_SECRET: 's',
        TURN_REALM: 'turn.example.com',
        TURN_URLS_OVERRIDE: 'turn:10.0.0.5:3478, turn:10.0.0.5:3478?transport=tcp',
      },
      now: () => FIXED_NOW,
    });
    expect(out!.urls).toEqual([
      'turn:10.0.0.5:3478',
      'turn:10.0.0.5:3478?transport=tcp',
    ]);
  });

  it('default TTL is 24h per the plan contract', () => {
    const out = generateTurnCredential({
      runId: 'abcdef12',
      env: { TURN_SECRET: 's' },
      now: () => FIXED_NOW,
    });
    expect(out!.ttl).toBe(86400);
    expect(out!.username.startsWith(String(FIXED_EPOCH + 86400) + ':')).toBe(true);
  });

  it('rejects non-positive TTLs eagerly', () => {
    expect(() => generateTurnCredential({
      runId: 'abcdef12',
      ttlSeconds: 0,
      env: { TURN_SECRET: 's' },
      now: () => FIXED_NOW,
    })).toThrow(/positive integer/);
    expect(() => generateTurnCredential({
      runId: 'abcdef12',
      ttlSeconds: -1,
      env: { TURN_SECRET: 's' },
      now: () => FIXED_NOW,
    })).toThrow(/positive integer/);
  });

  it('rejects an empty runId', () => {
    expect(() => generateTurnCredential({
      runId: '',
      env: { TURN_SECRET: 's' },
      now: () => FIXED_NOW,
    })).toThrow(/runId is required/);
  });

  it('passes a non-uuid runId through unchanged in the username tag', () => {
    const out = generateTurnCredential({
      runId: 'plain-tag',
      env: { TURN_SECRET: 's' },
      now: () => FIXED_NOW,
    });
    expect(out!.username.endsWith(':plain')).toBe(true);
  });
});
