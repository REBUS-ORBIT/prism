<script setup lang="ts">
import { computed } from 'vue';
import type { JobSummary } from '../../shared/api';

const props = defineProps<{ jobs: JobSummary[] }>();

const sorted = computed(() => [...props.jobs].sort((a, b) => b.createdAt.localeCompare(a.createdAt)));

function fmtSize(b: number): string {
  if (!b) return '—';
  const units = ['B', 'KB', 'MB', 'GB'];
  let v = b, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(v >= 100 ? 0 : 1)} ${units[i]}`;
}
function shortId(id: string): string { return id.slice(0, 8); }
</script>

<template>
  <table>
    <thead>
      <tr>
        <th>Status</th>
        <th>File</th>
        <th>Size</th>
        <th>Target</th>
        <th>Workstation</th>
        <th>Progress</th>
        <th>Created</th>
        <th>Job</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="j in sorted" :key="j.id">
        <td><span class="pill" :class="j.status">{{ j.status }}</span></td>
        <td>{{ j.fileName }} <span class="muted">{{ j.format }}</span></td>
        <td>{{ fmtSize(j.fileSize) }}</td>
        <td>{{ j.orbitTarget }} <span class="muted">{{ j.modelName || j.modelId }}</span></td>
        <td>{{ j.nodeName ?? '—' }}</td>
        <td>
          <div v-if="j.progressPercent != null" class="progress">
            <div class="fill" :style="{ width: `${j.progressPercent}%` }"></div>
          </div>
          <span v-else class="muted">—</span>
          <div v-if="j.lastMessage" class="muted" style="font-size: 11px;">{{ j.lastMessage }}</div>
        </td>
        <td class="muted">{{ new Date(j.createdAt).toLocaleString() }}</td>
        <td><code>{{ shortId(j.id) }}</code></td>
      </tr>
      <tr v-if="!sorted.length">
        <td colspan="8" class="muted" style="text-align:center; padding: 24px;">no jobs yet</td>
      </tr>
    </tbody>
  </table>
</template>
