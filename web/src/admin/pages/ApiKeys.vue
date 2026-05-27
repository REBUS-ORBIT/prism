<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { keysApi, type ApiKey, type ApiError } from '../../shared/api';

const keys = ref<ApiKey[]>([]);
const knownScopes = ref<string[]>([]);
const showNew = ref(false);
const newName = ref('');
const newRpm  = ref<number | undefined>(undefined);
const newQuota = ref<number | undefined>(undefined);
const newScopes = ref<Record<string, boolean>>({});
const mintedPlaintext = ref<string | null>(null);
const error = ref<string | null>(null);

// Edit-scopes modal state — opened per row via the Edit button.
const editing = ref<ApiKey | null>(null);
const editScopes = ref<Record<string, boolean>>({});

/**
 * Friendly label for the known scope strings. New scopes added on the
 * server should also get an entry here so the checkbox set in the
 * create form reads sensibly. Unknown scopes fall through to the raw id.
 */
function scopeLabel(scope: string): string {
  const labels: Record<string, string> = {
    'visualiser:create_stream': 'Visualiser — create stream',
  };
  return labels[scope] ?? scope;
}

async function refresh() {
  keys.value = (await keysApi.list()).keys;
}

async function loadScopes() {
  try {
    knownScopes.value = (await keysApi.scopes()).scopes;
    // Reset the create-form checkbox bag whenever the scope catalog refreshes.
    newScopes.value = Object.fromEntries(knownScopes.value.map((s) => [s, false]));
  } catch {
    knownScopes.value = [];
  }
}

function checkedScopes(bag: Record<string, boolean>): string[] {
  return Object.entries(bag).filter(([, v]) => v).map(([k]) => k);
}

async function create() {
  error.value = null;
  try {
    const r = await keysApi.create({
      name: newName.value,
      rateLimitPerMin: newRpm.value,
      monthlyQuota: newQuota.value,
      scopes: checkedScopes(newScopes.value),
    });
    mintedPlaintext.value = r.plaintext;
    newName.value = ''; newRpm.value = undefined; newQuota.value = undefined;
    newScopes.value = Object.fromEntries(knownScopes.value.map((s) => [s, false]));
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

function startEdit(k: ApiKey) {
  editing.value = k;
  editScopes.value = Object.fromEntries(
    knownScopes.value.map((s) => [s, k.scopes.includes(s)]),
  );
}

function cancelEdit() {
  editing.value = null;
  editScopes.value = {};
}

async function saveEdit() {
  if (!editing.value) return;
  try {
    await keysApi.patch(editing.value.id, { scopes: checkedScopes(editScopes.value) });
    cancelEdit();
    await refresh();
  } catch (err) {
    error.value = (err as ApiError).message ?? 'save failed';
  }
}

onMounted(async () => {
  await loadScopes();
  await refresh();
});
</script>

<template>
  <div class="h-row">
    <h1 class="flex-1">API keys</h1>
    <button class="primary" @click="showNew = true">+ New key</button>
  </div>
  <p class="muted">
    External <code>/v1/*</code> callers authenticate with these. Plaintext is shown ONCE at creation; we only store the SHA-256 hash.
    Send integrators to the <a href="/docs/" target="_blank" rel="noopener">API reference</a> for full endpoint documentation.
  </p>

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
    </div>
    <div v-if="knownScopes.length" class="scope-row mt-sm">
      <span class="muted scope-label">Scopes:</span>
      <label v-for="s in knownScopes" :key="s" class="scope-chk">
        <input type="checkbox" v-model="newScopes[s]" />
        <span>{{ scopeLabel(s) }}</span>
      </label>
    </div>
    <div class="h-row mt-sm">
      <span class="flex-1"></span>
      <button class="primary" :disabled="!newName" @click="create">Create</button>
      <button @click="showNew = false">Cancel</button>
    </div>
  </div>

  <div class="card mt">
    <table>
      <thead>
        <tr><th>Name</th><th>Scopes</th><th>Rate / min</th><th>Monthly quota</th><th>Last used</th><th>Status</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="k in keys" :key="k.id">
          <td>{{ k.name }}</td>
          <td>
            <span v-if="!k.scopes.length" class="muted">—</span>
            <span v-else class="scope-pill-row">
              <span v-for="s in k.scopes" :key="s" class="pill scope-pill">{{ s }}</span>
            </span>
          </td>
          <td>{{ k.rateLimitPerMin ?? '—' }}</td>
          <td>{{ k.monthlyQuota ?? '—' }}</td>
          <td class="muted">{{ k.lastUsedAt ? new Date(k.lastUsedAt).toLocaleString() : 'never' }}</td>
          <td><span class="pill" :class="k.isActive ? 'online' : 'offline'">{{ k.isActive ? 'active' : 'disabled' }}</span></td>
          <td>
            <button @click="startEdit(k)">Edit scopes</button>
            <button @click="toggle(k)" style="margin-left: 4px;">{{ k.isActive ? 'Disable' : 'Enable' }}</button>
            <button @click="remove(k)" style="margin-left: 4px;">Delete</button>
          </td>
        </tr>
        <tr v-if="!keys.length"><td colspan="7" class="muted" style="text-align:center; padding: 24px;">no keys</td></tr>
      </tbody>
    </table>
  </div>

  <div v-if="editing" class="modal-overlay" @click.self="cancelEdit">
    <div class="card modal-card">
      <h3 class="mt-0">Edit scopes — {{ editing.name }}</h3>
      <p class="muted">Toggle which capabilities this key is allowed to call.</p>
      <div v-if="knownScopes.length" class="scope-row">
        <label v-for="s in knownScopes" :key="s" class="scope-chk">
          <input type="checkbox" v-model="editScopes[s]" />
          <span>{{ scopeLabel(s) }}</span>
        </label>
      </div>
      <p v-else class="muted">No scopes defined.</p>
      <div class="h-row mt">
        <span class="flex-1"></span>
        <button @click="cancelEdit">Cancel</button>
        <button class="primary" @click="saveEdit" style="margin-left: 4px;">Save</button>
      </div>
    </div>
  </div>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0; }
.scope-row { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.scope-label { font-size: 13px; }
.scope-chk { display: inline-flex; align-items: center; gap: 4px; font-size: 13px; cursor: pointer; }
.scope-pill-row { display: inline-flex; flex-wrap: wrap; gap: 4px; }
.pill.scope-pill { background: var(--orbit-primary-fade, var(--color-bg-hover)); color: var(--orbit-primary, var(--color-text)); font-size: 11px; }
.modal-overlay {
  position: fixed; inset: 0; background: rgba(0,0,0,0.4);
  display: flex; align-items: center; justify-content: center; z-index: 9999;
}
.modal-card { min-width: 360px; max-width: 520px; }
</style>
