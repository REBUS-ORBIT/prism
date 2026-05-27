/**
 * Typed view of the agent <-> server WS protocol.
 *
 * This file is hand-maintained and the canonical TypeScript source.
 * It mirrors `shared/contracts/agent-protocol.json` exactly — the JSON
 * Schema is canonical wire-format documentation (used by ajv for runtime
 * validation and as input to C# codegen). When you edit the schema,
 * update this file and `shared/contracts/AgentProtocol.cs` in the same
 * commit. `npm run validate:contracts` checks consistency in CI.
 *
 * Versioned: bump the `Envelope.v` literal for breaking changes.
 */

export const PROTOCOL_VERSION = 1 as const;

export type MessageType =
  | 'hello'
  | 'welcome'
  | 'server_ping'
  | 'heartbeat'
  | 'assign'
  | 'ack'
  | 'progress'
  | 'log'
  | 'complete'
  | 'fail'
  | 'cancel'
  | 'pollLayers'
  | 'layers'
  | 'restart'
  | 'update'
  | 'startVisualisation'
  | 'cancelVisualisation'
  | 'visualisationReady'
  | 'visualisationFailed'
  | 'visualisationEnded'
  | 'signallingFrame';

export type AgentRole = 'conversion' | 'layering' | 'receive' | 'visualiser';

export interface HelloData {
  machineId: string;
  nodeName: string;
  slots: number;
  formats: string[];
  roles: AgentRole[];
  agentVersion: string;
  rhinoVersion?: string;
}

export interface WelcomeData {
  sessionId: string;
  serverTime: string;
  heartbeatSeconds?: number;
}

export interface HeartbeatData {
  slotsBusy: number;
  cpuPct?: number;
  memUsedMb?: number;
}

export type JobKind = 'convert' | 'receive';

export interface AssignData {
  jobId: string;
  jobType?: JobKind;          // default 'convert' for backwards compat with Phase 3 agents
  slot: number;
  format: string;
  fileUrl?: string;           // not set for `receive`
  fileName?: string;
  orbitServerUrl: string;
  orbitToken: string;
  projectId: string;
  modelId: string;
  modelName?: string;
  receiveVersionId?: string;  // required when jobType === 'receive'
  outputFormats?: string[];   // ['3dm','step','ifc','glb']; for receive: primary output is outputFormats[0]
  outputUploadUrl?: string;   // POST endpoint the agent uses to deliver non-ORBIT outputs back to the server
  options?: {
    swapYZ?: boolean;
    quality?: 'sensible' | 'extreme';
    includedLayers?: string[];
    includeLayerDescendants?: boolean;
  };
}

export interface AckData {
  jobId: string;
  accepted: boolean;
  reason?: string;
}

export interface ProgressData {
  jobId: string;
  stage: string;
  percent?: number;
  message?: string;
}

export interface LogData {
  jobId: string;
  level: 'debug' | 'info' | 'warn' | 'error';
  message: string;
}

export interface CompleteData {
  jobId: string;
  versionUrl?: string;                 // primary ORBIT URL; omitted for pure receive jobs
  rootObjectId?: string;
  versionId?: string;
  outputs?: Record<string, string>;    // additional output file URLs by format ('3dm','glb',...)
  stats?: {
    objects?: number;
    blobs?: number;
    uploadBytes?: number;
    elapsedMs?: number;
  };
}

export interface FailData {
  jobId: string;
  error: string;
  stack?: string;
  retryable?: boolean;
}

export interface CancelData {
  jobId: string;
  reason?: string;
}

export interface PollLayersData {
  jobId: string;
  fileUrl: string;
  format: string;
}

/**
 * Server -> agent: cleanly exit the agent process. The Windows Scheduled
 * Task's `RestartCount=3` / `RestartInterval=1m` auto-respawns it; the
 * agent itself also schedules a small helper script that relaunches the
 * EXE after the process exits, so respawn is robust regardless of how
 * the task scheduler is configured. Reason is optional and surfaced in
 * agent logs.
 */
export interface RestartData {
  reason?: string;
}

/**
 * Server -> agent: check for a new release on GitHub Releases and apply
 * it if one is available. Reuses `Updater.CheckForUpdateAsync` +
 * `DownloadAndInstallAsync` — the same code path as the tray's
 * "Check for updates" menu item. `tag` is optional; when omitted the
 * agent picks the latest release.
 */
export interface UpdateData {
  tag?: string;
}

export interface LayerNode {
  name: string;
  fullPath?: string;
  color?: string;
  visible?: boolean;
  children?: LayerNode[];
}

export interface LayersData {
  jobId: string;
  layers: LayerNode[];
}

/* -------------------------------------------------------------------------- */
/* Visualiser (Phase A scaffold — orchestrator lands in Phase F/G)            */
/* -------------------------------------------------------------------------- */

/**
 * Phase J — pointer to a portal-uploaded project attachment (MVR / GDTF /
 * generic blob). The PRISM server owns the bytes; the orchestrator fetches
 * them with the same bearer token it uses for ORBIT (or a dedicated PRISM
 * token already present in the agent's HTTP client). `downloadUrl` is the
 * absolute URL the orchestrator should GET; `sizeBytes` lets it pre-allocate
 * progress reporting and short-circuit oversized payloads.
 *
 * Optional `contentType` is the value the portal recorded at upload time —
 * useful for the orchestrator's MvrGdtfDetector when the extension is
 * ambiguous (e.g. `.zip` archives that hold a `.mvr`).
 */
export interface ProjectAttachmentRef {
  id: string;
  filename: string;
  contentType?: string;
  sizeBytes: number;
  downloadUrl: string;
}

/**
 * Server -> agent: spin up a Pixel Streaming session for an ORBIT version.
 * The agent imports the model into an Unreal template build, starts the
 * stream, and asynchronously replies with `visualisationReady` (or
 * `visualisationFailed`).
 */
export interface StartVisualisationData {
  runId: string;
  slot: number;
  orbitServerUrl: string;
  orbitToken: string;
  projectId: string;
  modelId: string;
  /** Optional ORBIT version to materialise. When omitted the agent picks the latest. */
  versionId?: string;
  /** UE template tag the orchestrator should run against (e.g. `v1.0.0-ue5.7`). */
  templateTag?: string;
  /** Public signalling URL the SPA will connect to; agent may echo it back when ready. */
  signallingUrl?: string;
  /** Max session lifetime in seconds; the orchestrator enforces hard tear-down at TTL. */
  ttlSeconds?: number;
  /**
   * Phase J — project-level lighting design attachments (MVR / GDTF) the
   * orchestrator should download and stage alongside the ORBIT glTF before
   * the visualiser starts. Omitted (not `[]`) when there are no
   * attachments, so older orchestrators that don't know about the field
   * keep working.
   */
  attachments?: ProjectAttachmentRef[];
}

/** Server -> agent: tear down a previously-started visualisation run. */
export interface CancelVisualisationData {
  runId: string;
  reason?: string;
}

/** Agent -> server: orchestrator is live; signalling URL is reachable. */
export interface VisualisationReadyData {
  runId: string;
  signallingUrl: string;
  streamerId?: string;
  expiresAt?: string;
}

/** Agent -> server: terminal failure during import / boot / streaming. */
export interface VisualisationFailedData {
  runId: string;
  error: string;
  stack?: string;
}

/**
 * Agent -> server: a previously-streaming run ended cleanly (TTL expired,
 * UE exited, browser disconnected, admin cancel). Terminal state — the
 * server moves the `visualiser_runs` row to `ended` and decrements the
 * workstation's `current_visualiser_load`.
 */
export interface VisualisationEndedData {
  runId: string;
  /** Free-form reason surfaced in admin SSE + the run row. */
  reason?: string;
}

/**
 * Bidirectional opaque WebRTC signalling envelope.
 *
 * Pixel Streaming 2's Cirrus signalling server speaks a JSON+binary
 * sub-protocol that PRISM does not parse. The server's
 * `signallingProxy.ts` wraps each browser frame into one of these
 * envelopes, the agent unwraps and forwards to the local Cirrus WS,
 * and the reverse direction does the same. Exactly one of `payload`
 * (text) / `payloadB64` (binary, base64-encoded) is set per frame.
 */
export interface SignallingFrameData {
  runId: string;
  /** UTF-8 text payload (Pixel Streaming JSON control frames). */
  payload?: string;
  /** Base64-encoded binary payload (Pixel Streaming media-control blobs). */
  payloadB64?: string;
}

/* -------------------------------------------------------------------------- */
/* Discriminated envelope union                                                */
/* -------------------------------------------------------------------------- */

interface Base<T extends MessageType, D> {
  v: typeof PROTOCOL_VERSION;
  type: T;
  id?: string;
  ts?: string;
  data: D;
}

export type HelloMsg      = Base<'hello',      HelloData>;
export type WelcomeMsg    = Base<'welcome',    WelcomeData>;
export type HeartbeatMsg  = Base<'heartbeat',  HeartbeatData>;
export type AssignMsg     = Base<'assign',     AssignData>;
export type AckMsg        = Base<'ack',        AckData>;
export type ProgressMsg   = Base<'progress',   ProgressData>;
export type LogMsg        = Base<'log',        LogData>;
export type CompleteMsg   = Base<'complete',   CompleteData>;
export type FailMsg       = Base<'fail',       FailData>;
export type CancelMsg     = Base<'cancel',     CancelData>;
export type PollLayersMsg = Base<'pollLayers', PollLayersData>;
export type LayersMsg     = Base<'layers',     LayersData>;
export type RestartMsg    = Base<'restart',    RestartData>;
export type UpdateMsg     = Base<'update',     UpdateData>;
export type StartVisualisationMsg  = Base<'startVisualisation',  StartVisualisationData>;
export type CancelVisualisationMsg = Base<'cancelVisualisation', CancelVisualisationData>;
export type VisualisationReadyMsg  = Base<'visualisationReady',  VisualisationReadyData>;
export type VisualisationFailedMsg = Base<'visualisationFailed', VisualisationFailedData>;
export type VisualisationEndedMsg  = Base<'visualisationEnded',  VisualisationEndedData>;
export type SignallingFrameMsg     = Base<'signallingFrame',     SignallingFrameData>;

export type AgentToServerMsg =
  | HelloMsg | HeartbeatMsg | AckMsg | ProgressMsg | LogMsg | CompleteMsg | FailMsg | LayersMsg
  | VisualisationReadyMsg | VisualisationFailedMsg | VisualisationEndedMsg | SignallingFrameMsg;

export type ServerToAgentMsg =
  | WelcomeMsg | AssignMsg | CancelMsg | PollLayersMsg | RestartMsg | UpdateMsg
  | StartVisualisationMsg | CancelVisualisationMsg | SignallingFrameMsg;

export type AnyMsg = AgentToServerMsg | ServerToAgentMsg;

/* -------------------------------------------------------------------------- */
/* Helpers                                                                     */
/* -------------------------------------------------------------------------- */

export function envelope<T extends MessageType, D>(type: T, data: D, id?: string): Base<T, D> {
  return { v: PROTOCOL_VERSION, type, id, ts: new Date().toISOString(), data };
}
