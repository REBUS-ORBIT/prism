<script setup lang="ts">
/**
 * Wraps @vue-flow/core to render a PipelineTopology with three live
 * overlays:
 *   - workstation pool nodes attached to the `workstation` stage
 *   - in-flight job badges + pulse animation on whichever stage each
 *     active job is currently sitting in
 *   - animated edges that highlight when traffic is flowing through
 *     them (i.e. either endpoint is hosting an active job)
 *
 * Read-only by default. Pass `editable` to enable drag-to-rearrange;
 * the parent listens to `@layout-change` to persist the new positions
 * via /api/settings/pipeline_layout_v1. The `savedLayout` prop is the
 * inverse: positions read out of settings on mount, applied here so
 * saved layouts survive reload.
 *
 * IMPLEMENTATION NOTE: live job/workstation data is applied via Vue
 * Flow's `updateNode` / `updateNodeData` rather than the `:nodes` prop
 * so that frequent WS-driven updates don't reset in-progress drag
 * positions. See watchNodesValue in @vue-flow/core: every time the
 * `:nodes` prop ref changes (i.e. our computed re-runs), `setNodes` is
 * invoked which Object.assigns each node prop OVER the live store node,
 * including `position` — which would snap a dragged node back mid-drag.
 * `updateNodeData` mutates only `node.data` in place and `updateNode`
 * (with a partial patch) mutates only the listed fields, both leaving
 * the live position untouched.
 */
import { computed, onMounted, ref, watch } from 'vue';
import { VueFlow, MarkerType, Handle, Position, useVueFlow, type Edge, type Node, type NodeDragEvent } from '@vue-flow/core';
import { Background } from '@vue-flow/background';
import { Controls } from '@vue-flow/controls';
import '@vue-flow/core/dist/style.css';
import '@vue-flow/core/dist/theme-default.css';
import '@vue-flow/controls/dist/style.css';
import { settingsApi, type JobSummary, type PipelineTopology, type Workstation } from '../../shared/api';
import { workstationWebUiHost, workstationWebUiUrl } from '../../shared/workstationUrl';

interface NodePos { x: number; y: number; }

// Optional DNS suffix appended to each workstation's `nodeName` when
// building the "Web UI ↗" link below; sourced from the
// `workstation_dns_suffix` admin setting. Fetched once on mount so
// frequent WS-driven re-renders don't churn the network. Operators
// must hard-reload after changing it in Settings. See
// shared/workstationUrl.ts for the URL format -- which now also
// honours `Workstation.host` (the live `agent_sessions.remote_addr`)
// and prefers it over the DNS suffix when present.
const dnsSuffix = ref<string>('');

function webUiHost(name: string, host?: string | null): string {
  return workstationWebUiHost(name, dnsSuffix.value, host);
}
function webUiUrl(name: string, host?: string | null): string {
  return workstationWebUiUrl(name, dnsSuffix.value, host);
}

const props = defineProps<{
  topology: PipelineTopology;
  workstations?: Workstation[];
  jobs?: JobSummary[];
  editable?: boolean;
  savedLayout?: Record<string, NodePos>;
}>();

const emit = defineEmits<{
  layoutChange: [positions: Record<string, NodePos>];
}>();

const { getNodes, updateNode, updateNodeData, findNode } = useVueFlow();

// ---------------------------------------------------------------------------
// Stage layout — saved positions win, otherwise auto-lay left-to-right.
// ---------------------------------------------------------------------------
const stagePositions = computed<Record<string, NodePos>>(() => {
  const widthPer = 200;
  const out: Record<string, NodePos> = {};
  props.topology.nodes.forEach((n, i) => {
    out[n.id] = props.savedLayout?.[n.id] ?? { x: 40 + i * widthPer, y: 80 };
  });
  return out;
});

// ---------------------------------------------------------------------------
// Map a job's currentStage / status onto a topology node id. The agent
// emits stage strings like 'downloading' / 'opening' / 'converting' /
// 'exporting-glb' that don't match topology node ids 1:1, so collapse
// every workstation-side stage onto the workstation node.
// ---------------------------------------------------------------------------
const SEND_WORKSTATION_STAGES = new Set([
  'downloading', 'extracting', 'opening', 'axis-swap',
  'preparing', 'converting',
]);
const RECEIVE_RHINO_STAGES = new Set(['receiving', 'hydrating', 'writing']);

function resolveTopologyStage(j: JobSummary, stageOrder: string[]): string | null {
  const cs = j.currentStage ?? '';

  // 1) Exact match on a topology node id (server-side stages do this).
  if (cs && stageOrder.includes(cs)) return cs;

  // 2) Send pipeline — agent-emitted stages collapse to 'workstation'.
  if (stageOrder.includes('workstation')) {
    if (SEND_WORKSTATION_STAGES.has(cs) || cs.startsWith('exporting')) return 'workstation';
  }

  // 3) Receive pipeline — agent-emitted stages collapse to 'receive-rhino'.
  if (stageOrder.includes('receive-rhino')) {
    if (RECEIVE_RHINO_STAGES.has(cs)) return 'receive-rhino';
  }

  // 4) Status-based fallback for stages that don't have an explicit stage label.
  if (j.status === 'uploading') {
    if (stageOrder.includes('upload')) return 'upload';
    if (stageOrder.includes('receive-deliver')) return 'receive-deliver';
  }
  if (j.status === 'queued') {
    if (stageOrder.includes('queue')) return 'queue';
    if (stageOrder.includes('receive-queue')) return 'receive-queue';
  }
  if (j.status === 'dispatched') {
    if (stageOrder.includes('dispatch')) return 'dispatch';
    if (stageOrder.includes('receive-dispatch')) return 'receive-dispatch';
  }
  if (j.status === 'processing') {
    if (stageOrder.includes('workstation')) return 'workstation';
    if (stageOrder.includes('receive-rhino')) return 'receive-rhino';
  }
  if (j.status === 'awaiting_selection' && stageOrder.includes('workstation')) {
    return 'workstation';
  }
  return null;
}

const stageJobs = computed<Record<string, JobSummary[]>>(() => {
  const stageOrder = props.topology.nodes.map((n) => n.id);
  const map: Record<string, JobSummary[]> = {};
  for (const id of stageOrder) map[id] = [];
  for (const j of props.jobs ?? []) {
    if (j.status === 'complete' || j.status === 'failed' || j.status === 'cancelled') continue;
    const stage = resolveTopologyStage(j, stageOrder);
    if (stage) map[stage].push(j);
  }
  return map;
});

const activeStages = computed<Set<string>>(() => {
  const s = new Set<string>();
  for (const [stage, list] of Object.entries(stageJobs.value)) {
    if (list.length > 0) s.add(stage);
  }
  return s;
});

// ---------------------------------------------------------------------------
// baseNodes — STRUCTURAL ONLY. Depends on topology + savedLayout + editable
// (i.e. only props that intentionally change positions). Live job/ws
// state is applied via the watch below using updateNode/updateNodeData
// so that mid-drag prop churn doesn't snap dragged nodes back.
// ---------------------------------------------------------------------------
const baseNodes = computed<Node[]>(() => {
  const draggable = !!props.editable;

  const stageNodes: Node[] = props.topology.nodes.map((n) => ({
    id: n.id,
    type: 'stage',
    position: stagePositions.value[n.id] ?? { x: 0, y: 0 },
    data: {
      label: n.label,
      kind: n.kind,
      optional: n.optional,
      count: 0,
      active: false,
      title: n.description,
    },
    sourcePosition: Position.Right,
    targetPosition: Position.Left,
    class: `stage stage-${n.kind}${n.optional ? ' optional' : ''}`,
    draggable,
  } satisfies Node));

  const wsStage = props.topology.nodes.find((n) => n.kind === 'workstation');
  const wsNodes: Node[] = wsStage && props.workstations
    ? props.workstations.map((w, i) => {
        const wsId = `ws-${w.id}`;
        const base = stagePositions.value[wsStage.id] ?? { x: 0, y: 0 };
        const pos = props.savedLayout?.[wsId] ?? { x: base.x - 10, y: base.y + 90 + i * 70 };
        return {
          id: wsId,
          type: 'ws',
          position: pos,
          data: {
            label: w.nodeName,
            sub: `${w.slotsBusy ?? 0}/${w.slotsTotal} slots`,
            online: !!w.online,
            busy: (w.slotsBusy ?? 0) > 0,
            title: w.nodeName,
            host: webUiHost(w.nodeName, w.host),
            webUiUrl: webUiUrl(w.nodeName, w.host),
          },
          class: `ws ws-offline`,
          draggable,
          sourcePosition: Position.Right,
          targetPosition: Position.Left,
        } satisfies Node;
      })
    : [];

  return [...stageNodes, ...wsNodes];
});

// ---------------------------------------------------------------------------
// Live state — applied via mutators so node positions are never touched.
// Runs once Vue Flow is initialised (@init) and then every time
// stageJobs / workstations change.
// ---------------------------------------------------------------------------
function applyLiveData() {
  const counts = stageJobs.value;
  for (const n of props.topology.nodes) {
    if (!findNode(n.id)) continue;
    const list = counts[n.id] ?? [];
    const count = list.length;
    const active = count > 0;
    const titleLines: string[] = [n.description];
    if (active) {
      titleLines.push('');
      titleLines.push(`${count} active job${count === 1 ? '' : 's'}:`);
      for (const j of list.slice(0, 6)) {
        titleLines.push(`• ${j.fileName} (${j.status}${j.currentStage ? ` / ${j.currentStage}` : ''})`);
      }
      if (count > 6) titleLines.push(`…and ${count - 6} more`);
    }
    updateNodeData(n.id, { count, active, title: titleLines.join('\n') });
    updateNode(n.id, {
      class: `stage stage-${n.kind}${n.optional ? ' optional' : ''}${active ? ' is-active' : ''}`,
    });
  }

  if (props.workstations) {
    for (const w of props.workstations) {
      const wsId = `ws-${w.id}`;
      if (!findNode(wsId)) continue;
      const busy = (w.slotsBusy ?? 0) > 0;
      const online = !!w.online;
      updateNodeData(wsId, {
        label: w.nodeName,
        sub: `${w.slotsBusy ?? 0}/${w.slotsTotal} slots`,
        online,
        busy,
        title: `${w.nodeName} — ${online ? 'online' : 'offline'} — ${w.slotsBusy ?? 0}/${w.slotsTotal} slots in use`,
        host: webUiHost(w.nodeName, w.host),
        webUiUrl: webUiUrl(w.nodeName, w.host),
      });
      updateNode(wsId, {
        class: `ws ws-${online ? 'online' : 'offline'}${busy ? ' busy' : ''}`,
      });
    }
  }
}

// React to live changes (jobs / workstations / dnsSuffix). The watcher
// fires only after Vue Flow's store has already absorbed baseNodes, so
// updateNode can find the targets. On initial render we also call
// applyLiveData() from the @init handler below to paint the first frame.
// `dnsSuffix` is included so the Web UI links update once the setting
// is fetched (it loads asynchronously on mount).
watch([stageJobs, () => props.workstations, dnsSuffix], () => applyLiveData(), { deep: true });
watch(() => props.topology, () => applyLiveData(), { flush: 'post' });

function onInit() {
  applyLiveData();
}

// Best-effort fetch of the optional DNS suffix admin setting. A failure
// (e.g. unauthenticated viewer of a public-embeddable pipeline view)
// just means we fall back to the bare-`nodeName` URL.
onMounted(async () => {
  try {
    const all = (await settingsApi.list()).settings;
    dnsSuffix.value = (all['workstation_dns_suffix'] ?? '').trim();
  } catch {
    dnsSuffix.value = '';
  }
});

// ---------------------------------------------------------------------------
// Edges — animate any edge whose source OR target is currently "active",
// so the traffic flow is visible both leaving and entering the busy stage.
// Edges have their own watch in @vue-flow/core (separate from nodes), so
// recomputing this prop does not disturb node positions.
// ---------------------------------------------------------------------------
const edges = computed<Edge[]>(() => {
  const active = activeStages.value;

  const stageEdges: Edge[] = props.topology.edges.map((e) => {
    const animated = active.has(e.from) || active.has(e.to);
    return {
      id: `${e.from}->${e.to}`,
      source: e.from,
      target: e.to,
      label: e.label,
      type: 'smoothstep',
      animated,
      markerEnd: { type: MarkerType.ArrowClosed, color: animated ? 'var(--orbit-primary)' : 'var(--color-border-strong)' },
      style: {
        stroke: animated ? 'var(--orbit-primary)' : 'var(--color-border-strong)',
        strokeWidth: animated ? 2.5 : 2,
      },
      class: animated ? 'edge-active' : '',
    } satisfies Edge;
  });

  const wsStage = props.topology.nodes.find((n) => n.kind === 'workstation');
  const wsEdges: Edge[] = wsStage && props.workstations
    ? props.workstations.map((w) => ({
        id: `ws-edge-${w.id}`,
        source: wsStage.id,
        target: `ws-${w.id}`,
        type: 'smoothstep',
        animated: (w.slotsBusy ?? 0) > 0,
        style: {
          stroke: w.online ? 'var(--orbit-primary)' : 'var(--color-border-strong)',
          strokeDasharray: '4 4',
          strokeWidth: 1,
        },
      } satisfies Edge))
    : [];

  return [...stageEdges, ...wsEdges];
});

// ---------------------------------------------------------------------------
// Drag persistence — when the user drops a node, snapshot every node's
// current position from Vue Flow's live store (NOT from baseNodes, which
// still reflects the pre-drag savedLayout) and emit. Parent debounces
// the PUT to /api/settings.
// ---------------------------------------------------------------------------
function onNodeDragStop(_evt: NodeDragEvent) {
  const positions: Record<string, NodePos> = {};
  for (const n of getNodes.value) {
    positions[n.id] = { x: Math.round(n.position.x), y: Math.round(n.position.y) };
  }
  emit('layoutChange', positions);
}
</script>

<template>
  <div class="flow-wrap">
    <VueFlow
      :nodes="baseNodes"
      :edges="edges"
      :fit-view-on-init="true"
      :nodes-draggable="!!editable"
      :nodes-connectable="false"
      :elements-selectable="!!editable"
      :zoom-on-double-click="false"
      @node-drag-stop="onNodeDragStop"
      @init="onInit"
    >
      <template #node-stage="nodeProps">
        <Handle type="target" :position="Position.Left" :connectable="false" />
        <div class="stage-node" :title="nodeProps.data.title">
          <div class="stage-node-row">
            <span class="stage-node-label">{{ nodeProps.data.label }}</span>
            <span v-if="nodeProps.data.count > 0" class="stage-node-badge">{{ nodeProps.data.count }}</span>
          </div>
        </div>
        <Handle type="source" :position="Position.Right" :connectable="false" />
      </template>

      <template #node-ws="nodeProps">
        <Handle type="target" :position="Position.Left" :connectable="false" />
        <div class="ws-node" :title="nodeProps.data.title">
          <div class="ws-node-label">{{ nodeProps.data.label }}</div>
          <div class="ws-node-sub">{{ nodeProps.data.sub }}</div>
          <a
            class="ws-node-link"
            :class="{ 'is-disabled': !nodeProps.data.online }"
            :href="nodeProps.data.online ? nodeProps.data.webUiUrl : undefined"
            target="_blank"
            rel="noopener noreferrer"
            :title="nodeProps.data.online
              ? `Opens ${nodeProps.data.webUiUrl} in a new tab — requires LAN DNS for ${nodeProps.data.host}.`
              : 'Agent offline'"
            @click.stop
          >Web UI ↗</a>
        </div>
        <Handle type="source" :position="Position.Right" :connectable="false" />
      </template>

      <Background pattern-color="var(--color-border)" :gap="20" />
      <Controls />
    </VueFlow>

    <div class="legend">
      <span class="dot online"></span> online
      <span class="dot busy"></span> active
      <span class="dot offline"></span> offline
    </div>
    <div v-if="editable" class="edit-hint">Drag any node to rearrange — positions save automatically.</div>
  </div>
</template>

<style scoped>
.flow-wrap {
  position: relative;
  width: 100%;
  height: 600px;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-lg);
  background: var(--color-bg);
}
.legend {
  position: absolute; bottom: 10px; right: 10px; z-index: 10;
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  padding: 6px 10px;
  font-size: 11px;
  display: flex; gap: 12px; align-items: center;
  color: var(--color-text-muted);
}
.dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 4px; }
.dot.online  { background: var(--color-success); }
.dot.busy    { background: var(--orbit-primary); }
.dot.offline { background: var(--color-text-subtle); }
.edit-hint {
  position: absolute; bottom: 10px; left: 10px; z-index: 10;
  background: var(--color-bg-elevated);
  border: 1px solid var(--orbit-primary);
  color: var(--orbit-primary);
  border-radius: var(--radius);
  padding: 6px 10px;
  font-size: 11px;
}
</style>

<style>
/* Global VueFlow node styles — must be unscoped so they apply to the
   nodes rendered inside the VueFlow shadow tree. */
.vue-flow__node.stage {
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius);
  padding: 8px 14px;
  font-size: 13px;
  font-weight: 500;
  min-width: 120px;
  text-align: center;
  color: var(--color-text);
  box-shadow: var(--shadow-1);
  transition: border-color 200ms ease, box-shadow 200ms ease, background 200ms ease;
}
.vue-flow__node.stage.optional { opacity: 0.7; border-style: dashed; }
.vue-flow__node.stage.stage-workstation {
  background: var(--orbit-primary-fade);
  border-color: var(--orbit-primary);
  color: var(--orbit-primary);
}
/* Active stage — pulse + brand-coloured glow. */
.vue-flow__node.stage.is-active {
  background: var(--orbit-primary-fade);
  border-color: var(--orbit-primary);
  color: var(--orbit-primary);
  box-shadow: 0 0 0 0 var(--orbit-primary);
  animation: prism-stage-pulse 1.6s ease-out infinite;
}
@keyframes prism-stage-pulse {
  0%   { box-shadow: 0 0 0 0     rgba(224, 98, 56, 0.55); }
  70%  { box-shadow: 0 0 0 10px  rgba(224, 98, 56, 0);    }
  100% { box-shadow: 0 0 0 0     rgba(224, 98, 56, 0);    }
}

.vue-flow__node.ws {
  background: var(--color-bg-elevated);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  padding: 6px 10px;
  font-size: 11px;
  min-width: 110px;
  color: var(--color-text-muted);
  text-align: center;
}
.vue-flow__node.ws.ws-online { border-color: var(--color-success); color: var(--color-success); }
.vue-flow__node.ws.ws-online.busy { background: var(--orbit-primary-fade); border-color: var(--orbit-primary); color: var(--orbit-primary); }
.vue-flow__node.ws.ws-offline { border-color: var(--color-border); color: var(--color-text-subtle); opacity: 0.7; }

/* Inner layout for the slot-rendered nodes. */
.stage-node-row {
  display: flex; align-items: center; justify-content: center; gap: 8px;
}
.stage-node-label { white-space: nowrap; }
.stage-node-badge {
  display: inline-flex; align-items: center; justify-content: center;
  min-width: 20px; height: 20px;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--orbit-primary);
  color: #fff;
  font-size: 11px;
  font-weight: 700;
  line-height: 1;
}
.ws-node-label { font-weight: 600; }
.ws-node-sub   { font-size: 10px; opacity: 0.85; margin-top: 2px; }
.ws-node-link {
  display: inline-block;
  margin-top: 4px;
  font-size: 10px;
  font-weight: 600;
  color: var(--orbit-primary);
  text-decoration: none;
  letter-spacing: 0.02em;
}
.ws-node-link:hover { text-decoration: underline; }
.ws-node-link.is-disabled {
  color: var(--color-text-muted);
  pointer-events: none;
  cursor: default;
  opacity: 0.6;
}

/* Edge animation tweak — keep the dash motion in brand orange when
   traffic is on the line. */
.vue-flow__edge.edge-active .vue-flow__edge-path {
  stroke: var(--orbit-primary) !important;
}
</style>
