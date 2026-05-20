<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { keysApi, type ApiKey, type ApiError } from '../../shared/api';

const keys = ref<ApiKey[]>([]);
const showNew = ref(false);
const newName = ref('');
const newRpm  = ref<number | undefined>(undefined);
const newQuota = ref<number | undefined>(undefined);
const mintedPlaintext = ref<string | null>(null);
const error = ref<string | null>(null);

async function refresh() {
  keys.value = (await keysApi.list()).keys;
}

async function create() {
  error.value = null;
  try {
    const r = await keysApi.create({ name: newName.value, rateLimitPerMin: newRpm.value, monthlyQuota: newQuota.value });
    mintedPlaintext.value = r.plaintext;
    newName.value = ''; newRpm.value = undefined; newQuota.value = undefined;
    showNew.value = false;
    await refresh();
  } catch (err) {
    error.value = (err as ApiError).message ?? 'create failed';
  }
}

async function toggle(k: ApiKey) {
  await keysApi.patch(k.id, { isActive: !k.isActive });
  await refresh();
}

async function remove(k: ApiKey) {
  if (!confirm(`Delete API key "${k.name}"? This cannot be undone.`)) return;
  await keysApi.remove(k.id);
  await refresh();
}

onMounted(refresh);
</script>

<template>
  <div class="h-row">
    <h1 class="flex-1">API keys</h1>
    <button class="primary" @click="showNew = true">+ New key</button>
  </div>
  <p class="muted">External /v1/* callers authenticate with these. Plaintext is shown ONCE at creation; we only store the SHA-256 hash.</p>

  <div v-if="error" class="error-box mt">{{ error }}</div>
  <div v-if="mintedPlaintext" class="card mt success-box">
    <strong>New key minted — copy now, you won't see it again:</strong>
    <pre style="margin: 8px 0 0; font-size: 12px; word-break: break-all;">{{ mintedPlaintext }}</pre>
    <div class="mt-sm"><button @click="mintedPlaintext = null">Dismiss</button></div>
  </div>

  <div v-if="showNew" class="card mt">
    <div class="h-row">
      <input v-model="newName" placeholder="Name (e.g. partner-acme-prod)" style="flex: 2;" />
      <input v-model.number="newRpm" type="number" min="1" placeholder="Rate / min" />
      <input v-model.number="newQuota" type="number" min="1" placeholder="Monthly quota" />
      <button class="primary" :disabled="!newName" @click="create">Create</button>
      <button @click="showNew = false">Cancel</button>
    </div>
  </div>

  <div class="card mt">
    <table>
      <thead>
        <tr><th>Name</th><th>Rate / min</th><th>Monthly quota</th><th>Last used</th><th>Status</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="k in keys" :key="k.id">
          <td>{{ k.name }}</td>
          <td>{{ k.rateLimitPerMin ?? '—' }}</td>
          <td>{{ k.monthlyQuota ?? '—' }}</td>
          <td class="muted">{{ k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleString() : 'never' }}</td>
          <td><span class="pill" :class="k.isActive ? 'online' : 'offline'">{{ k.isActive ? 'active' : 'disabled' }}</span></td>
          <td>
            <button @click="toggle(k)">{{ k.isActive ? 'Disable' : 'Enable' }}</button>
            <button @click="remove(k)" style="margin-left: 4px;">Delete</button>
          </td>
        </tr>
        <tr v-if="!keys.length"><td colspan="6" class="muted" style="text-align:center; padding: 24px;">no keys</td></tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0; }
</style>
