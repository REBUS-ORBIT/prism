<script setup lang="ts">
/**
 * Admin Visualiser page (Phase G — start / poll / stop).
 *
 * Renders a table of recent runs + a "Start new stream" modal. Polls
 * `/api/visualiser/streams` every 5s while any non-terminal run exists;
 * stops polling once everything settles. Same pattern as Dashboard.
 *
 * Phase I will replace the "Open viewer" link target with a real Pixel
 * Streaming player; for now it's a minimal iframe (VisualiserViewer.vue).
 */
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { RouterLink } from 'vue-router';
import OrbitPicker from '../../shared/OrbitPicker.vue';
import {
  orbitApi,
  visualiserApi,
  type ApiError,
  type VisualiserRun,
  type VisualiserStatus,
  type VisualiserWorkstation,
} from '../../shared/api';

const rows = ref<VisualiserRun[]>([]);
const loading = ref(true);
const errorMsg = ref<string | null>(null);

let pollTimer: ReturnType<typeof setInterval> | null = null;

const NON_TERMINAL: VisualiserStatus[] = ['queued', 'importing', 'streaming'];
const NON_TERMINAL_SET = new Set<VisualiserStatus>(NON_TERMINAL);

function hasActiveRuns(): boolean {
  return rows.value.some((r) => NON_TERMINAL_SET.has(r.status));
}

async function refresh() {
  try {
    rows.value = (await visualiserApi.listStreams({ limit: 50 })).runs;
    errorMsg.value = null;
  } catch (err) {
    errorMsg.value = (err as ApiError).message ?? 'failed to load runs';
  } finally {
    loading.value = false;
  }
}

function startPollIfNeeded() {
  if (pollTimer || !hasActiveRuns()) return;
  pollTimer = setInterval(async () => {
    try {
      rows.value = (await visualiserApi.listStreams({ limit: 50 })).runs;
    } catch { /* keep polling */ }
    if (!hasActiveRuns()) {
      clearInterval(pollTimer!);
      pollTimer = null;
    }
  }, 5_000);
}

function stopPoll() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

// ---------------------------------------------- live duration ticker
// Tick every second so `durationLive` re-renders on the streaming rows.
// Avoids a watcher per row; the table just reads `now.value` in a computed.
const now = ref(Date.now());
let nowTimer: ReturnType<typeof setInterval> | null = null;

function formatDuration(ms: number): string {
  if (ms < 0 || !Number.isFinite(ms)) return '—';
  const s = Math.floor(ms / 1000);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return `${h}h ${m.toString().padStart(2, '0')}m ${sec.toString().padStart(2, '0')}s`;
  return `${m}m ${sec.toString().padStart(2, '0')}s`;
}

function durationLive(r: VisualiserRun): string {
  if (r.endedAt) return formatDuration(new Date(r.endedAt).getTime() - new Date(r.createdAt).getTime());
  return formatDuration(now.value - new Date(r.createdAt).getTime());
}

// ---------------------------------------------- ORBIT project / model lookup cache
// Resolves projectId → name on first sighting and caches client-side. ORBIT
// has no batch endpoint so we deduplicate via Promise.all on the unique IDs
// in the current row set.
const projectNames = ref<Record<string, string>>({});
const projectLookupTarget = ref<'prod' | 'dev'>('prod');

async function refreshProjectNames() {
  // Pre-loading the full project list under the current target is much faster
  // than per-row fetches, which would also hit ORBIT rate limits with >10
  // active runs.
  try {
    const r = await orbitApi.projects(projectLookupTarget.value, 500);
    const next: Record<string, string> = {};
    for (const p of r.items) next[p.id] = p.name;
    projectNames.value = next;
  } catch {
    // Silent — column just shows the raw ID, which is what the previous PRISM
    // pages do when ORBIT isn't reachable.
  }
}

function projectNameFor(id: string): string {
  return projectNames.value[id] ?? id;
}

// ---------------------------------------------- actions
const stoppingIds = ref(new Set<string>());

async function stopRun(r: VisualiserRun) {
  if (stoppingIds.value.has(r.id)) return;
  if (!confirm(`Stop visualiser run for ${projectNameFor(r.projectId)} / ${r.modelId}?`)) return;
  stoppingIds.value.add(r.id);
  try {
    await visualiserApi.stopStream(r.id);
    await refresh();
    startPollIfNeeded();
  } catch (err) {
    errorMsg.value = (err as ApiError).message ?? 'stop failed';
  } finally {
    stoppingIds.value.delete(r.id);
  }
}

// ---------------------------------------------- start-stream modal
const showStart = ref(false);
const startTarget = ref<'prod' | 'dev'>('prod');
const startProjectId = ref('');
const startModelId = ref('');
const startModelName = ref('');
const startVersionId = ref('');
const startWorkstations = ref<VisualiserWorkstation[]>([]);
const startPickedWorkstation = ref<string>('');
const starting = ref(false);
const startError = ref<string | null>(null);

const canStart = computed(() =>
  !!startProjectId.value && !!startModelId.value && !starting.value,
);

async function openStartModal() {
  showStart.value = true;
  startError.value = null;
  startProjectId.value = '';
  startModelId.value = '';
  startModelName.value = '';
  startVersionId.value = '';
  startPickedWorkstation.value = '';
  try {
    startWorkstations.value = (await visualiserApi.listWorkstations()).workstations;
  } catch (err) {
    startError.value = (err as ApiError).message ?? 'failed to list workstations';
  }
}

function closeStartModal() {
  if (starting.value) return;
  showStart.value = false;
}

async function submitStart() {
  if (!canStart.value) return;
  starting.value = true;
  startError.value = null;
  try {
    const r = await visualiserApi.startStream({
      projectId: startProjectId.value,
      modelId:   startModelId.value,
      versionId: startVersionId.value || undefined,
      orbitTarget: startTarget.value,
      preferredWorkstationId: startPickedWorkstation.value || undefined,
    });
    showStart.value = false;
    await refresh();
    startPollIfNeeded();
    // If we got a runId back, sync the project name cache so the new row
    // doesn't appear as a bare GUID for a 5s polling tick.
    void refreshProjectNames();
    // Hop straight into the viewer.
    if (r.runId) {
      window.location.hash = `#/visualiser/${r.runId}`;
    }
  } catch (err) {
    const e = err as ApiError;
    const body = (e.body as { code?: string; message?: string } | undefined) ?? {};
    startError.value = body.message ?? body.code ?? e.message ?? 'start failed';
  } finally {
    starting.value = false;
  }
}

// ---------------------------------------------- lifecycle
onMounted(async () => {
  await Promise.all([refresh(), refreshProjectNames()]);
  startPollIfNeeded();
  nowTimer = setInterval(() => { now.value = Date.now(); }, 1000);
});

onUnmounted(() => {
  stopPoll();
  if (nowTimer) clearInterval(nowTimer);
});
</script>

<template>
  <section>
    <header class="page-head">
      <div>
        <h1>Visualiser</h1>
        <p class="muted">
          Pixel Streaming sessions of ORBIT versions. Streams run on
          <code>canVisualise = true</code> workstations.
        </p>
      </div>
      <button class="btn btn-primary" @click="openStartModal">+ Start new stream</button>
    </header>

    <div v-if="errorMsg" class="alert err">{{ errorMsg }}</div>

    <div v-if="loading" class="muted">Loading…</div>

    <table v-else-if="rows.length" class="table">
      <thead>
        <tr>
          <th>Run</th>
          <th>Project</th>
          <th>Model</th>
          <th>Version</th>
          <th>Workstation</th>
          <th>Status</th>
          <th>Started</th>
          <th>Ready</th>
          <th>Duration</th>
          <th class="row-actions">Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in rows" :key="r.id">
          <td><code class="mono">{{ r.id.slice(0, 8) }}</code></td>
          <td>{{ projectNameFor(r.projectId) }}</td>
          <td><code class="mono">{{ r.modelId.slice(0, 8) }}</code></td>
          <td>{{ r.versionId ? r.versionId.slice(0, 8) : '—' }}</td>
          <td>{{ r.workstationId ? r.workstationId.slice(0, 8) : '—' }}</td>
          <td>
            <span :class="['pill', `pill--${r.status}`]">{{ r.status }}</span>
            <span v-if="r.failureReason" class="muted" style="margin-left:6px"
                  :title="r.error ?? r.failureReason">
              ({{ r.failureReason }})
            </span>
          </td>
          <td>{{ new Date(r.createdAt).toLocaleTimeString() }}</td>
          <td>{{ r.readyAt ? new Date(r.readyAt).toLocaleTimeString() : '—' }}</td>
          <td>{{ durationLive(r) }}</td>
          <td class="row-actions">
            <RouterLink
              v-if="r.status === 'streaming'"
              :to="{ name: 'visualiser-viewer', params: { runId: r.id } }"
              class="btn btn-sm"
            >Open viewer</RouterLink>
            <button
              v-if="NON_TERMINAL.includes(r.status)"
              class="btn btn-sm btn-danger"
              :disabled="stoppingIds.has(r.id)"
              @click="stopRun(r)"
            >{{ stoppingIds.has(r.id) ? 'Stopping…' : 'Stop' }}</button>
          </td>
        </tr>
      </tbody>
    </table>

    <p v-else class="muted">No visualiser runs yet. Click <strong>Start new stream</strong> to begin.</p>

    <!-- Start new stream modal -->
    <div v-if="showStart" class="modal-backdrop" @click.self="closeStartModal">
      <div class="modal">
        <header>
          <h2>Start visualiser stream</h2>
          <button class="btn-close" :disabled="starting" @click="closeStartModal">×</button>
        </header>

        <div class="form">
          <label class="form-row">
            <span>ORBIT target</span>
            <select v-model="startTarget" :disabled="starting">
              <option value="prod">prod</option>
              <option value="dev">dev</option>
            </select>
          </label>

          <OrbitPicker
            :target="startTarget"
            :project-id="startProjectId"
            :model-id="startModelId"
            :model-name="startModelName"
            @update:projectId="startProjectId = $event"
            @update:modelId="startModelId = $event"
            @update:modelName="startModelName = $event"
          />

          <label class="form-row">
            <span>Version ID <span class="muted">(optional — latest if blank)</span></span>
            <input v-model="startVersionId" :disabled="starting" placeholder="v_2026_05_…" />
          </label>

          <label class="form-row">
            <span>Workstation <span class="muted">(optional — least-loaded if blank)</span></span>
            <select v-model="startPickedWorkstation" :disabled="starting">
              <option value="">— auto-pick least loaded —</option>
              <option
                v-for="w in startWorkstations"
                :key="w.id"
                :value="w.id"
                :disabled="!w.online || !w.canVisualise"
              >
                {{ w.nodeName }}
                <template v-if="!w.online"> · offline</template>
                <template v-else-if="!w.canVisualise"> · canVisualise=false</template>
                <template v-else> · load {{ w.currentVisualiserLoad }} / {{ w.slotsTotal }}</template>
              </option>
            </select>
          </label>

          <div v-if="startError" class="alert err">{{ startError }}</div>

          <p class="muted small">
            Synchronous: blocks ~2-3 s (warm) / ~60-90 s (cold start)
            while the orchestrator boots UE and reports ready.
          </p>
        </div>

        <footer>
          <button class="btn" :disabled="starting" @click="closeStartModal">Cancel</button>
          <button class="btn btn-primary" :disabled="!canStart" @click="submitStart">
            {{ starting ? 'Starting…' : 'Start stream' }}
          </button>
        </footer>
      </div>
    </div>
  </section>
</template>

<style scoped>
.page-head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; }
.page-head p { margin: 4px 0 0; font-size: 13px; }

.muted { color: var(--color-text-muted); }
.small { font-size: 12px; }
.mono  { font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace); }

.alert.err {
  border: 1px solid var(--color-danger, #c33);
  background: var(--color-danger-fade, rgba(204,51,51,0.08));
  padding: 8px 12px; border-radius: var(--radius); margin-bottom: 12px;
}

.table { width: 100%; border-collapse: collapse; }
.table th, .table td {
  text-align: left; padding: 6px 8px;
  border-bottom: 1px solid var(--color-border);
  font-size: 13px; vertical-align: middle;
}
.row-actions { white-space: nowrap; }
.row-actions .btn + .btn { margin-left: 4px; }

.pill {
  display: inline-block; padding: 2px 8px;
  border-radius: 999px; font-size: 11px; font-weight: 600;
  background: var(--color-bg-elevated); border: 1px solid var(--color-border);
}
.pill--streaming { background: rgba(64,160,96,0.15); border-color: rgba(64,160,96,0.4); color: rgb(64,160,96); }
.pill--importing,
.pill--queued    { background: rgba(220,160,64,0.15); border-color: rgba(220,160,64,0.4); color: rgb(196,140,40); }
.pill--failed    { background: rgba(204,51,51,0.15);  border-color: rgba(204,51,51,0.4);  color: rgb(204,80,80); }
.pill--ended     { color: var(--color-text-muted); }

.btn {
  padding: 4px 10px; border-radius: var(--radius); cursor: pointer;
  border: 1px solid var(--color-border); background: var(--color-bg-elevated); color: var(--color-text);
  font-size: 13px;
}
.btn:hover:not(:disabled) { background: var(--color-bg); }
.btn:disabled { opacity: 0.6; cursor: not-allowed; }
.btn-sm   { padding: 3px 8px; font-size: 12px; }
.btn-primary { background: var(--orbit-primary); color: white; border-color: var(--orbit-primary); }
.btn-primary:hover:not(:disabled) { background: var(--orbit-primary-hover, var(--orbit-primary)); }
.btn-danger  { color: var(--color-danger, #c33); }

/* Modal */
.modal-backdrop {
  position: fixed; inset: 0; background: rgba(0,0,0,0.45);
  display: flex; align-items: center; justify-content: center; z-index: 100;
}
.modal {
  background: var(--color-bg); border: 1px solid var(--color-border);
  border-radius: var(--radius); width: 520px; max-width: 90vw;
  display: flex; flex-direction: column; box-shadow: 0 12px 40px rgba(0,0,0,0.35);
}
.modal header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 12px 16px; border-bottom: 1px solid var(--color-border);
}
.modal header h2 { font-size: 16px; margin: 0; }
.btn-close {
  background: transparent; border: none; font-size: 22px; line-height: 1; cursor: pointer;
  color: var(--color-text-muted);
}
.modal .form { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
.form-row { display: flex; flex-direction: column; gap: 4px; font-size: 13px; }
.form-row input, .form-row select {
  padding: 6px 8px; border-radius: var(--radius);
  border: 1px solid var(--color-border); background: var(--color-bg); color: var(--color-text);
  font-size: 13px;
}
.modal footer {
  display: flex; gap: 8px; justify-content: flex-end;
  padding: 12px 16px; border-top: 1px solid var(--color-border);
}
</style>
