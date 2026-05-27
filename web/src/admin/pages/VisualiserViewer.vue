<script setup lang="ts">
/**
 * Minimal viewer page (Phase G).
 *
 * Renders an `<iframe>` pointing at the orchestrator's player URL. Phase I
 * replaces this with a real Pixel Streaming JS embed (driving the
 * signalling WS proxy at `/ws/visualiser/:runId/signalling` with a token
 * minted from `visualiserApi.signallingToken`).
 *
 * Until then we just surface the same URL the orchestrator returned in
 * `ready/v1` — that page already speaks the Cirrus signalling protocol
 * directly when opened on the workstation, so on the workstation itself
 * the iframe works end-to-end. From elsewhere it'll fail to fetch
 * `signallingUrl: ws://127.0.0.1:<port>/` — Phase I fixes that.
 */
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { useRoute, RouterLink } from 'vue-router';
import { visualiserApi, type ApiError, type VisualiserRun } from '../../shared/api';

const route = useRoute();
const runId = computed(() => String(route.params.runId ?? ''));

const run = ref<VisualiserRun | null>(null);
const loadError = ref<string | null>(null);
const iframeReady = ref(false);

let pollTimer: ReturnType<typeof setInterval> | null = null;

async function refresh() {
  try {
    run.value = await visualiserApi.getStream(runId.value);
    loadError.value = null;
  } catch (err) {
    loadError.value = (err as ApiError).message ?? 'failed to load run';
  }
}

onMounted(async () => {
  await refresh();
  pollTimer = setInterval(refresh, 5_000);
});

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer);
});

async function stopRun() {
  if (!run.value) return;
  if (!confirm('Stop this visualiser run?')) return;
  try {
    await visualiserApi.stopStream(run.value.id);
    await refresh();
  } catch (err) {
    loadError.value = (err as ApiError).message ?? 'stop failed';
  }
}
</script>

<template>
  <section class="viewer">
    <header class="viewer-head">
      <div>
        <RouterLink :to="{ name: 'visualiser' }" class="back">← Back to streams</RouterLink>
        <h1>Visualiser <code class="mono">{{ runId.slice(0, 8) }}</code></h1>
        <p class="muted small">
          <template v-if="run">
            status <span :class="['pill', `pill--${run.status}`]">{{ run.status }}</span>
            <template v-if="run.workstationId">
              · workstation <code class="mono">{{ run.workstationId.slice(0, 8) }}</code>
            </template>
            <template v-if="run.failureReason">
              · failure <code class="mono">{{ run.failureReason }}</code>
            </template>
          </template>
        </p>
      </div>
      <div class="head-actions" v-if="run && (run.status === 'streaming' || run.status === 'importing' || run.status === 'queued')">
        <button class="btn btn-danger" @click="stopRun">Stop</button>
      </div>
    </header>

    <div v-if="loadError" class="alert err">{{ loadError }}</div>

    <div v-if="run && run.status === 'streaming' && run.playerUrl" class="iframe-shell">
      <div v-if="!iframeReady" class="overlay">Loading player…</div>
      <iframe
        :src="run.playerUrl"
        allow="autoplay; fullscreen; gamepad; xr-spatial-tracking; clipboard-write"
        allowfullscreen
        @load="iframeReady = true"
      />
      <p class="muted small phase-i-note">
        Phase G placeholder — Phase I will replace this iframe with a real
        Pixel Streaming embed driven by
        <code>/ws/visualiser/{{ runId }}/signalling</code>.
      </p>
    </div>

    <div v-else-if="run" class="placeholder">
      <p v-if="run.status === 'streaming' && !run.playerUrl">
        Run reported <code>streaming</code> but the orchestrator did not
        return a <code>playerUrl</code>. This is a bug — please report it.
      </p>
      <p v-else-if="run.status === 'queued' || run.status === 'importing'">
        Stream is <strong>{{ run.status }}</strong>. The page will switch to
        the player once the agent reports <code>visualisationReady</code>.
      </p>
      <p v-else-if="run.status === 'failed'">
        Run failed: <code>{{ run.failureReason ?? run.error ?? 'unknown' }}</code>.
      </p>
      <p v-else>Run ended.</p>
    </div>
  </section>
</template>

<style scoped>
.viewer { display: flex; flex-direction: column; gap: 16px; }
.viewer-head { display: flex; justify-content: space-between; align-items: flex-end; }
.viewer-head h1 { margin: 0; font-size: 18px; }
.back { font-size: 12px; color: var(--color-text-muted); text-decoration: none; }
.back:hover { color: var(--color-text); }
.muted { color: var(--color-text-muted); }
.small { font-size: 12px; }
.mono  { font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace); }

.iframe-shell {
  position: relative; flex: 1 1 0; min-height: 0;
  display: flex; flex-direction: column; gap: 6px;
}
.iframe-shell iframe {
  width: 100%; aspect-ratio: 16 / 9; border: 1px solid var(--color-border);
  border-radius: var(--radius); background: #000;
}
.overlay {
  position: absolute; inset: 0; display: flex; align-items: center; justify-content: center;
  background: rgba(0,0,0,0.6); color: white; font-weight: 600; border-radius: var(--radius);
  pointer-events: none;
}
.phase-i-note { margin: 0; }
.placeholder {
  padding: 32px; border: 1px dashed var(--color-border); border-radius: var(--radius);
  text-align: center; color: var(--color-text-muted);
}

.alert.err {
  border: 1px solid var(--color-danger, #c33);
  background: var(--color-danger-fade, rgba(204,51,51,0.08));
  padding: 8px 12px; border-radius: var(--radius);
}

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
  padding: 6px 12px; border-radius: var(--radius); cursor: pointer;
  border: 1px solid var(--color-border); background: var(--color-bg-elevated); color: var(--color-text);
  font-size: 13px;
}
.btn:hover { background: var(--color-bg); }
.btn-danger { color: var(--color-danger, #c33); }
</style>
