<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { workstationsApi, type Workstation } from '../../shared/api';

const rows = ref<Workstation[]>([]);
const loading = ref(true);

async function refresh() {
  rows.value = (await workstationsApi.list()).workstations;
  loading.value = false;
}

async function toggleEnabled(w: Workstation) {
  const updated = await workstationsApi.update(w.id, { isEnabled: !w.isEnabled });
  Object.assign(w, updated);
}

async function remove(w: Workstation) {
  if (!confirm(`Delete workstation "${w.nodeName}"?`)) return;
  await workstationsApi.remove(w.id);
  await refresh();
}

onMounted(refresh);
</script>

<template>
  <h1>Workstations</h1>
  <p class="muted">Agent connections register themselves on first connect. Toggle enabled to gate dispatch.</p>

  <div class="card mt">
    <table v-if="!loading">
      <thead>
        <tr>
          <th>Status</th>
          <th>Node</th>
          <th>Slots</th>
          <th>Formats</th>
          <th>Roles</th>
          <th>Agent</th>
          <th>Rhino</th>
          <th>Last seen</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="w in rows" :key="w.id">
          <td><span class="pill" :class="w.online ? 'online' : 'offline'">{{ w.online ? 'online' : 'offline' }}</span></td>
          <td>
            <div>{{ w.nodeName }}</div>
            <div class="muted" style="font-size: 11px;"><code>{{ w.machineId }}</code></div>
          </td>
          <td>{{ w.slotsBusy ?? 0 }} / {{ w.slotsTotal }}</td>
          <td><code>{{ w.supportedFormats.join(' ') }}</code></td>
          <td>
            <span v-if="w.canConvert">convert</span>
            <span v-if="w.canLayer"> · layer</span>
            <span v-if="w.canReceive"> · receive</span>
          </td>
          <td class="muted">{{ w.agentVersion ?? '—' }}</td>
          <td class="muted">{{ w.rhinoVersion ?? '—' }}</td>
          <td class="muted">{{ w.lastSeenAt ? new Date(w.lastSeenAt).toLocaleString() : '—' }}</td>
          <td>
            <button @click="toggleEnabled(w)">{{ w.isEnabled ? 'Disable' : 'Enable' }}</button>
            <button @click="remove(w)" style="margin-left: 4px;">Delete</button>
          </td>
        </tr>
        <tr v-if="!rows.length">
          <td colspan="9" class="muted" style="text-align: center; padding: 32px;">
            No workstations registered. Install PRISM.Agent on a Rhino host and it'll appear here.
          </td>
        </tr>
      </tbody>
    </table>
    <div v-else class="muted">loading…</div>
  </div>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0 0 8px; }
</style>
