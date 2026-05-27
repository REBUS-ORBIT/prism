<script setup lang="ts">
/**
 * Admin Pipeline page.
 *
 * Renders the static topology from /api/pipelines and overlays:
 *   - live in-flight jobs (badges + pulse animations on the active stage,
 *     animated edges around the active node)
 *   - workstation pool nodes attached to the workstation stage
 *
 * Layout is persistable: the "Edit layout" toggle enables drag, and
 * dropped positions are debounce-saved to /api/settings/pipeline_layout_v1.
 * "Reset layout" wipes the saved positions for the currently-selected
 * pipeline. Topology (the set of nodes/edges) remains static — driven
 * by server/src/conversion/pipelines.ts. Topology editing would require
 * a server-side mutation API + dispatcher rewrite; left as a follow-up.
 */
import { computed, onMounted, onUnmounted, ref, shallowRef } from 'vue';
import { jobsApi, pipelinesApi, settingsApi, workstationsApi, type JobSummary, type PipelineTopology, type Workstation } from '../../shared/api';
import { adminWs } from '../../shared/ws';
import FlowEditor from '../components/FlowEditor.vue';

const LAYOUT_SETTING_KEY = 'pipeline_layout_v1';
const SAVE_DEBOUNCE_MS = 400;

interface NodePos { x: number; y: number; }
type LayoutMap = Record<string, Record<string, NodePos>>;

const topologies = shallowRef<Record<string, PipelineTopology>>({});
const selected = ref<string>('send');
const workstations = ref<Workstation[]>([]);
const jobs = ref<JobSummary[]>([]);
const loading = ref(true);

const editable = ref(false);
const savedLayouts = ref<LayoutMap>({});
const saveStatus = ref<'idle' | 'saving' | 'saved' | 'error'>('idle');

let unsub: (() => void) | null = null;
let saveTimer: ReturnType<typeof setTimeout> | null = null;
let pollTimer: ReturnType<typeof setInterval> | null = null;

const NON_TERMINAL = new Set<string>(['queued', 'dispatched', 'processing', 'uploading', 'awaiting_selection']);

const currentLayout = computed<Record<string, NodePos>>(() => savedLayouts.value[selected.value] ?? {});
const hasSavedLayout = computed(() => Object.keys(currentLayout.value).length > 0);
const activeJobCount = computed(() => jobs.value.filter((j) => NON_TERMINAL.has(j.status)).length);

function parseLayouts(raw: string | undefined): LayoutMap {
  if (!raw) return {};
  try {
    const parsed = JSON.parse(raw);
    if (parsed && typeof parsed === 'object') return parsed as LayoutMap;
  } catch { /* corrupt — start fresh */ }
  return {};
}

async function loadLayouts() {
  try {
    const res = await settingsApi.list();
    savedLayouts.value = parseLayouts(res.settings[LAYOUT_SETTING_KEY]);
  } catch { /* tolerated — falls back to auto-layout */ }
}

async function refresh() {
  const [p, w, j] = await Promise.all([
    pipelinesApi.list(),
    workstationsApi.list(),
    jobsApi.list({ limit: 100 }),
  ]);
  topologies.value = p.pipelines;
  if (!topologies.value[selected.value]) selected.value = Object.keys(topologies.value)[0] ?? 'send';
  workstations.value = w.workstations;
  jobs.value = j.jobs;
  loading.value = false;
  startPollIfNeeded();
}

function hasActiveJobs(): boolean {
  return jobs.value.some((j) => NON_TERMINAL.has(j.status));
}

function startPollIfNeeded() {
  if (pollTimer || !hasActiveJobs()) return;
  pollTimer = setInterval(async () => {
    try {
      const [j, w] = await Promise.all([
        jobsApi.list({ limit: 100 }),
        workstationsApi.list(),
      ]);
      jobs.value = j.jobs;
      workstations.value = w.workstations;
    } catch { /* keep polling */ }
    if (!hasActiveJobs()) {
      clearInterval(pollTimer!);
      pollTimer = null;
    }
  }, 5_000);
}

function persistLayout(map: LayoutMap) {
  if (saveTimer) clearTimeout(saveTimer);
  saveStatus.value = 'saving';
  saveTimer = setTimeout(async () => {
    try {
      await settingsApi.set(LAYOUT_SETTING_KEY, JSON.stringify(map));
      saveStatus.value = 'saved';
      setTimeout(() => { if (saveStatus.value === 'saved') saveStatus.value = 'idle'; }, 1500);
    } catch {
      saveStatus.value = 'error';
    }
  }, SAVE_DEBOUNCE_MS);
}

function onLayoutChange(positions: Record<string, NodePos>) {
  const next: LayoutMap = { ...savedLayouts.value, [selected.value]: positions };
  savedLayouts.value = next;
  persistLayout(next);
}

async function resetLayout() {
  const next: LayoutMap = { ...savedLayouts.value };
  delete next[selected.value];
  savedLayouts.value = next;
  if (saveTimer) { clearTimeout(saveTimer); saveTimer = null; }
  saveStatus.value = 'saving';
  try {
    await settingsApi.set(LAYOUT_SETTING_KEY, JSON.stringify(next));
    saveStatus.value = 'saved';
    setTimeout(() => { if (saveStatus.value === 'saved') saveStatus.value = 'idle'; }, 1500);
  } catch {
    saveStatus.value = 'error';
  }
}

onMounted(async () => {
  await Promise.all([refresh(), loadLayouts()]);
  unsub = adminWs.on((ev) => {
    if (ev.type === 'job') {
      const idx = jobs.value.findIndex((j) => j.id === ev['jobId']);
      if (idx === -1) {
        // New job we haven't seen — refresh for the full row.
        void refresh();
        return;
      }
      const patch: Partial<JobSummary> = {};
      if (typeof ev['status']           === 'string') patch.status         = ev['status'] as JobSummary['status'];
      if (typeof ev['currentStage']     === 'string') patch.currentStage   = ev['currentStage'] as string;
      if (typeof ev['progressPercent']  === 'number') patch.progressPercent= ev['progressPercent'] as number;
      if (typeof ev['lastMessage']      === 'string') patch.lastMessage    = ev['lastMessage'] as string;
      if (typeof ev['nodeName']         === 'string') patch.nodeName       = ev['nodeName'] as string;
      jobs.value = jobs.value.map((j, i) => (i === idx ? { ...j, ...patch } : j));
      startPollIfNeeded();
    } else if (ev.type === 'workstation') {
      void refresh();
    }
  });
});

onUnmounted(() => {
  unsub?.();
  if (saveTimer) clearTimeout(saveTimer);
  if (pollTimer) clearInterval(pollTimer);
});
</script>

<template>
  <div class="h-row toolbar">
    <h1 class="flex-1">Pipeline</h1>
    <span class="muted in-flight">{{ activeJobCount }} in flight</span>
    <span v-if="saveStatus === 'saving'" class="muted save-status">saving…</span>
    <span v-else-if="saveStatus === 'saved'" class="muted save-status">layout saved</span>
    <span v-else-if="saveStatus === 'error'" class="error-status">save failed</span>
    <button
      :class="{ primary: editable }"
      :title="editable ? 'Lock node positions' : 'Drag nodes to rearrange'"
      @click="editable = !editable"
    >{{ editable ? 'Done editing' : 'Edit layout' }}</button>
    <button
      :disabled="!hasSavedLayout"
      :title="hasSavedLayout ? 'Restore the auto-layout for this pipeline' : 'No custom layout to reset'"
      @click="resetLayout"
    >Reset layout</button>
    <select v-model="selected">
      <option v-for="(_, id) in topologies" :key="id" :value="id">{{ String(id) }}</option>
    </select>
  </div>
  <p class="muted">Live view: stages from <code>server/src/conversion/pipelines.ts</code>, workstation pool nodes from the live agent registry. Active stages pulse in <span class="brand">brand orange</span>; the badge shows how many jobs are currently sitting on each step.</p>

  <div v-if="loading" class="card mt"><div class="muted">loading…</div></div>
  <div v-else class="mt">
    <FlowEditor
      v-if="topologies[selected]"
      :key="selected"
      :topology="topologies[selected]"
      :workstations="workstations"
      :jobs="jobs"
      :editable="editable"
      :saved-layout="currentLayout"
      @layout-change="onLayoutChange"
    />
    <div v-else class="card muted">no topology</div>
  </div>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0 0 8px; }
.toolbar { gap: 8px; flex-wrap: wrap; }
.in-flight { font-size: 12px; }
.save-status { font-size: 12px; font-style: italic; }
.error-status { font-size: 12px; color: var(--color-error); }
.brand { color: var(--orbit-primary); font-weight: 600; }
</style>
