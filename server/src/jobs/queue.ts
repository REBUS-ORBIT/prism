/**
 * BullMQ job queue.
 *
 * Phase 1: queue declaration only. Phase 2 attaches a Worker that hands
 * jobs to the dispatcher; phase 3 the dispatcher pushes to a live agent.
 */
import { Queue, QueueEvents } from 'bullmq';
import { bullConnection } from './redis.js';

export type ConvertJobPayload = {
  jobId: string;       // FK to jobs.id (the canonical row)
  format: string;
  fileName: string;
  filePath: string;
  orbitTarget: 'prod' | 'dev';
  projectId: string;
  modelId: string;
  modelName?: string;
  callbackUrl?: string;
  submittedBy?: string;
};

export const CONVERT_QUEUE = 'prism-convert';

export const convertQueue = new Queue<ConvertJobPayload>(CONVERT_QUEUE, {
  connection: bullConnection,
  defaultJobOptions: {
    attempts: 1,                                  // PRISM handles retry policy explicitly
    removeOnComplete: { age: 60 * 60, count: 1000 },
    removeOnFail:     { age: 24 * 60 * 60 },
  },
});

export const convertQueueEvents = new QueueEvents(CONVERT_QUEUE, { connection: bullConnection });

export async function enqueueConvert(payload: ConvertJobPayload): Promise<string> {
  const job = await convertQueue.add('convert', payload, { jobId: payload.jobId });
  return job.id!;
}
