<script setup lang="ts">
/**
 * Admin debug viewer for a single visualiser run (Phase I).
 *
 * Phase G shipped this page with an `<iframe>` shim pointed at the
 * orchestrator's local Cirrus URL — usable on the workstation itself
 * but broken from any other origin because `ws://127.0.0.1:<port>/`
 * isn't reachable. Phase I replaces it with a real Pixel Streaming
 * embed driven by PRISM's WS signalling proxy (`signallingProxy.ts`),
 * which terminates the browser's signalling WebSocket at the server,
 * authenticates with a short-lived HS256 JWT, and forwards each frame
 * across the agent WS to the agent's local Cirrus bridge.
 */
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { useRoute, RouterLink } from 'vue-router';
import { visualiserApi, type ApiError, type VisualiserRun } from '../../shared/api';
import PixelStreamingPlayer from '../components/PixelStreamingPlayer.vue';

const route = useRoute();
const runId = computed(() => String(route.params.runId ?? ''));

const run = ref<VisualiserRun | null>(null);
const loadError = ref<string | null>(null);

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
  // Poll only the metadata; the WebRTC stream is its own long-lived
  // connection. Slower poll than Phase G's 5s — once the player is
  // attached, the status pill rarely changes.
  pollTimer = setInterval(refresh, 10_000);
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
            <template v-if="run.turn === null">
              · <span class="muted">TURN unset (LAN only)</span>
            </template>
          </template>
        </p>
      </div>
      <div class="head-actions" v-if="run && (run.status === 'streaming' || run.status === 'importing' || run.status === 'queued')">
        <button class="btn btn-danger" @click="stopRun">Stop</button>
      </div>
    </header>

    <div v-if="loadError" class="alert err">{{ loadError }}</div>

    <div v-if="run && run.status === 'streaming' && run.signallingUrl" class="player-shell">
      <PixelStreamingPlayer
        :run-id="runId"
        :signalling-url="run.signallingUrl"
        :turn="run.turn ?? null"
      />
    </div>

    <div v-else-if="run" class="placeholder">
      <p v-if="run.status === 'streaming' && !run.signallingUrl">
        Run reported <code>streaming</code> but the server did not
        return a <code>signallingUrl</code>. This is a bug — please report it.
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
.viewer { display: flex; flex-direction: column; gap: 16px; height: 100%; min-height: 0; }
.viewer-head { display: flex; justify-content: space-between; align-items: flex-end; }
.viewer-head h1 { margin: 0; font-size: 18px; }
.back { font-size: 12px; color: var(--color-text-muted); text-decoration: none; }
.back:hover { color: var(--color-text); }
.muted { color: var(--color-text-muted); }
.small { font-size: 12px; }
.mono  { font-family: var(--font-mono, ui-monospace, SFMono-Regular, monospace); }

.player-shell {
  flex: 1 1 0;
  min-height: 0;
  display: flex;
  flex-direction: column;
}
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
