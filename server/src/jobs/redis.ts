/**
 * Shared Redis connections.
 *
 * BullMQ requires `maxRetriesPerRequest: null` on its connections so we
 * keep a dedicated client per role.
 */
import Redis, { type RedisOptions } from 'ioredis';

const REDIS_URL = process.env.REDIS_URL ?? 'redis://localhost:6379';

function build(opts: RedisOptions = {}): Redis {
  return new Redis(REDIS_URL, {
    enableReadyCheck: true,
    lazyConnect: false,
    ...opts,
  });
}

// BullMQ client / subscriber sockets (long-lived; must keep retrying)
export const bullConnection = build({ maxRetriesPerRequest: null });
export const bullSubscriber = build({ maxRetriesPerRequest: null });

// General-purpose Redis client for rate limiting, transient session state, etc.
export const redis = build({ maxRetriesPerRequest: 3 });
