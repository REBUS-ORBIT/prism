<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { settingsApi, type ApiError } from '../../shared/api';

interface Row { key: string; value: string; original: string; secret: boolean; dirty: boolean; }

const known = [
  { key: 'orbit_server_url',     label: 'ORBIT prod URL',       secret: false },
  { key: 'orbit_dev_server_url', label: 'ORBIT dev URL',        secret: false },
  { key: 'orbit_token',          label: 'Shared ORBIT token',   secret: true  },
  { key: 'orbit_dev_token',      label: 'Shared ORBIT dev token', secret: true },
  { key: 'job_retention_hours',  label: 'Job retention (hours)', secret: false },
  { key: 'maintenance_mode',     label: 'Maintenance mode (0/1)', secret: false },
];

const rows = ref<Row[]>(known.map((k) => ({ key: k.key, value: '', original: '', secret: k.secret, dirty: false })));
const status = ref<string | null>(null);
const error = ref<string | null>(null);

async function refresh() {
  const res = await settingsApi.list();
  const all = res.settings;
  for (const r of rows.value) {
    r.value = all[r.key] ?? '';
    r.original = r.value;
    r.dirty = false;
  }
}

async function save(r: Row) {
  error.value = null;
  try {
    await settingsApi.set(r.key, r.value);
    r.original = r.value;
    r.dirty = false;
    status.value = `saved ${r.key}`;
    setTimeout(() => (status.value = null), 1500);
  } catch (err) {
    error.value = (err as ApiError).message ?? 'save failed';
  }
}

onMounted(refresh);
</script>

<template>
  <h1>Settings</h1>
  <p class="muted">Live values used by the orchestrator + dispatcher. Secrets are masked after saving.</p>

  <div v-if="error" class="error-box mt">{{ error }}</div>
  <div v-if="status" class="success-box mt">{{ status }}</div>

  <div class="card mt">
    <div class="row" v-for="r in rows" :key="r.key">
      <label>
        {{ known.find(k => k.key === r.key)?.label ?? r.key }}
        <code class="muted">{{ r.key }}</code>
      </label>
      <input
        :type="r.secret ? 'password' : 'text'"
        v-model="r.value"
        @input="r.dirty = r.value !== r.original"
        :placeholder="r.secret ? '••••••' : ''"
      />
      <button class="primary" :disabled="!r.dirty" @click="save(r)">Save</button>
    </div>
  </div>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0 0 8px; }
.row { display: grid; grid-template-columns: 220px 1fr auto; gap: 12px; align-items: center; padding: 10px 0; border-bottom: 1px solid var(--color-border); }
.row:last-child { border-bottom: none; }
label { display: flex; flex-direction: column; gap: 2px; font-weight: 500; }
label code { font-size: 11px; font-weight: 400; }
</style>
