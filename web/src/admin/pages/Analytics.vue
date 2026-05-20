<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { jobsApi, type JobSummary } from '../../shared/api';

const jobs = ref<JobSummary[]>([]);

onMounted(async () => {
  jobs.value = (await jobsApi.list({ limit: 500 })).jobs;
});

const totalJobs = computed(() => jobs.value.length);
const totalBytes = computed(() => jobs.value.reduce((acc, j) => acc + (j.fileSize ?? 0), 0));
const byStatus = computed(() => {
  const m: Record<string, number> = {};
  for (const j of jobs.value) m[j.status] = (m[j.status] ?? 0) + 1;
  return m;
});
const byFormat = computed(() => {
  const m: Record<string, number> = {};
  for (const j of jobs.value) m[j.format] = (m[j.format] ?? 0) + 1;
  return Object.entries(m).sort((a, b) => b[1] - a[1]);
});

function fmtBytes(b: number): string {
  if (!b) return '0 B';
  const u = ['B','KB','MB','GB','TB']; let v = b, i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 ? 0 : 1)} ${u[i]}`;
}
</script>

<template>
  <h1>Analytics</h1>
  <p class="muted">Aggregated from the last 500 jobs. A dedicated time-window picker arrives in a follow-up.</p>

  <div class="stats mt">
    <div class="card stat"><div class="muted">Total jobs</div><div class="num">{{ totalJobs }}</div></div>
    <div class="card stat"><div class="muted">Bytes processed</div><div class="num">{{ fmtBytes(totalBytes) }}</div></div>
    <div class="card stat"><div class="muted">By status</div>
      <div class="mt-sm">
        <span v-for="(n, s) in byStatus" :key="s" class="pill" :class="s" style="margin-right: 4px;">{{ s }} · {{ n }}</span>
      </div>
    </div>
  </div>

  <div class="card mt">
    <h2>By format</h2>
    <table>
      <thead><tr><th>Format</th><th>Jobs</th></tr></thead>
      <tbody>
        <tr v-for="[fmt, n] in byFormat" :key="fmt"><td><code>{{ fmt }}</code></td><td>{{ n }}</td></tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0 0 8px; }
h2 { font-size: 16px; margin: 0 0 12px; }
.stats { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; }
.stat .num { font-size: 28px; font-weight: 700; margin-top: 6px; }
</style>
