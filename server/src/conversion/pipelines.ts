/**
 * Static pipeline topology.
 *
 * The flow editor renders this DAG; the dispatcher uses it as the
 * canonical sequence of stages a job moves through. Adding a new stage
 * here automatically:
 *   - shows up in the admin Pipeline view
 *   - becomes a valid `currentStage` value on jobs
 *   - is exposed via /api/conversion/pipeline
 *
 * Workstations are NOT modelled here as nodes — they're rendered as a
 * live overlay around the Dispatch -> Workstation -> Upload edge.
 */

export type StageKind = 'ingest' | 'validate' | 'preconvert' | 'queue' | 'dispatch' | 'workstation' | 'upload' | 'notify' | 'receive';

export interface PipelineNode {
  id: string;
  kind: StageKind;
  label: string;
  description: string;
  optional?: boolean;
}

export interface PipelineEdge {
  from: string;
  to: string;
  label?: string;
}

export interface PipelineTopology {
  nodes: PipelineNode[];
  edges: PipelineEdge[];
}

export const sendPipeline: PipelineTopology = {
  nodes: [
    { id: 'ingest',      kind: 'ingest',      label: 'Ingest',          description: 'Accept upload via /api/convert/async or /v1/convert' },
    { id: 'validate',    kind: 'validate',    label: 'Validate',        description: 'Extension whitelist + size limits + format sniff' },
    { id: 'preconvert',  kind: 'preconvert',  label: 'Pre-convert',     description: 'Route Assimp formats (gltf/glb/dae/blend/x/usdz) through prism-assimp -> OBJ+MTL+textures.zip', optional: true },
    { id: 'queue',       kind: 'queue',       label: 'Queue',           description: 'BullMQ convert queue, prioritised by user/key' },
    { id: 'dispatch',    kind: 'dispatch',    label: 'Dispatch',        description: 'Pick least-loaded agent slot with matching role + format' },
    { id: 'workstation', kind: 'workstation', label: 'Workstation',     description: 'Rhino opens file, runs OrbitConnector send pipeline' },
    { id: 'upload',      kind: 'upload',      label: 'ORBIT upload',    description: 'Agent posts objects + blobs directly to orbit-server' },
    { id: 'notify',      kind: 'notify',      label: 'Notify',          description: 'WS push + webhook callback (when configured)', optional: true },
  ],
  edges: [
    { from: 'ingest',      to: 'validate'    },
    { from: 'validate',    to: 'preconvert'  },
    { from: 'preconvert',  to: 'queue'       },
    { from: 'queue',       to: 'dispatch'    },
    { from: 'dispatch',    to: 'workstation' },
    { from: 'workstation', to: 'upload'      },
    { from: 'upload',      to: 'notify'      },
  ],
};

export const receivePipeline: PipelineTopology = {
  nodes: [
    { id: 'receive-request',  kind: 'ingest',      label: 'Request',         description: 'GET /api/receive triggers a receive job with a target ORBIT version' },
    { id: 'receive-queue',    kind: 'queue',       label: 'Queue',           description: 'BullMQ receive queue' },
    { id: 'receive-dispatch', kind: 'dispatch',    label: 'Dispatch',        description: 'Pick agent slot with the receive role' },
    { id: 'receive-rhino',    kind: 'workstation', label: 'Rhino',           description: 'Pull objects from ORBIT, hydrate raw encoding, write .3dm / .step' },
    { id: 'receive-deliver',  kind: 'upload',      label: 'Deliver',         description: 'Stream output back to caller' },
  ],
  edges: [
    { from: 'receive-request',  to: 'receive-queue'    },
    { from: 'receive-queue',    to: 'receive-dispatch' },
    { from: 'receive-dispatch', to: 'receive-rhino'    },
    { from: 'receive-rhino',    to: 'receive-deliver'  },
  ],
};

export const PIPELINES = {
  send:    sendPipeline,
  receive: receivePipeline,
} as const;
export type PipelineId = keyof typeof PIPELINES;
