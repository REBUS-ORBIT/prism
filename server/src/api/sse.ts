/**
 * /api/jobs/:id/stream — Server-Sent Events feed for one job.
 *
 * On open, emits the job's current state, then a `log`/`progress` event
 * each time the agent pushes a frame. Falls back to polling-driven
 * updates when the agent goes away.
 *
 * Uses the same in-memory adminProtocol broadcaster — every event
 * already published to admin subscribers also goes here, filtered by
 * jobId.
 */
import type { FastifyPluginAsync } from 'fastify';
import { eq } from 'drizzle-orm';
import { db } from '../db/client.js';
import { jobs } from '../db/schema.js';
import { sessionRegistry } from '../ws/sessionRegistry.js';
import { requireAuth } from '../auth/middleware.js';

const plugin: FastifyPluginAsync = async (app) => {
  app.addHook('preHandler', requireAuth);

  app.get<{ Params: { id: string } }>('/:id/stream', async (req, reply) => {
    const jobId = req.params.id;
    const job = await db.query.jobs.findFirst({ where: eq(jobs.id, jobId) });
    if (!job) return reply.code(404).send({ error: 'not found' });

    reply.raw.setHeader('content-type', 'text/event-stream');
    reply.raw.setHeader('cache-control', 'no-cache, no-transform');
    reply.raw.setHeader('connection', 'keep-alive');
    reply.raw.setHeader('x-accel-buffering', 'no');  // disable nginx-style buffering at Caddy
    reply.hijack();

    // Initial snapshot
    send(reply.raw, 'state', JSON.stringify({
      id: job.id,
      status: job.status,
      currentStage: job.currentStage,
      progressPercent: job.progressPercent,
      lastMessage: job.lastMessage,
      resultUrl: job.resultUrl,
    }));

    const topic = `job:${jobId}`;
    const onFrame = (frame: string) => send(reply.raw, 'update', frame);

    // Hook into the admin broadcast: add a transient subscriber.
    const transientId = `sse-${jobId}-${Math.random().toString(36).slice(2)}`;
    sessionRegistry.addAdmin({
      id: transientId,
      // we don't actually own a socket — the broadcast code calls socket.send,
      // so we cheat with a minimal stand-in.
      // @ts-expect-error — duck-typed socket
      socket: { send: onFrame },
      connectedAt: new Date(),
      subscriptions: new Set([topic]),
    });

    // Keep-alive ping every 25s so proxies don't time out.
    const ka = setInterval(() => {
      try { reply.raw.write(': keepalive\n\n'); } catch { /* socket gone */ }
    }, 25_000);

    req.raw.on('close', () => {
      clearInterval(ka);
      sessionRegistry.removeAdmin(transientId);
      try { reply.raw.end(); } catch { /* ignore */ }
    });
  });
};

function send(raw: NodeJS.WritableStream, event: string, data: string) {
  try {
    raw.write(`event: ${event}\n`);
    raw.write(`data: ${data}\n\n`);
  } catch {
    /* socket gone, the close handler will tidy up */
  }
}

export default plugin;
