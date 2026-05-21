<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue';
import { RouterLink } from 'vue-router';
import {
  workstationsApi,
  type AgentBuildInfo,
  type AgentConfigTemplate,
  type ApiError,
  type Workstation,
} from '../../shared/api';
import { adminWs } from '../../shared/ws';

const rows = ref<Workstation[]>([]);
const loading = ref(true);

// Tracks in-flight role toggles as a `${workstationId}:${role}` set so we can
// dim the pill while the PATCH is in flight and ignore double-clicks.
const inFlightRoleEdits = reactive(new Set<string>());

let unsubscribeWs: (() => void) | null = null;
let pollTimer: ReturnType<typeof setInterval> | null = null;

// ---------------------------------------------------------------- downloads
const agentInfo = ref<AgentBuildInfo | null>(null);
const agentInfoError = ref<string | null>(null);

// Per-node config generator state
const configNodeName = ref('');
const configSlots    = ref<number>(2);
const configRoles    = ref<Record<'conversion' | 'layering' | 'receive', boolean>>({
  conversion: true,
  layering: true,
  receive: true,
});
const previewedConfig = ref<AgentConfigTemplate | null>(null);
const previewError    = ref<string | null>(null);
const copied          = ref(false);

const previewedConfigText = computed(() =>
  previewedConfig.value ? JSON.stringify(previewedConfig.value, null, 2) : '',
);

const selectedRoles = computed(() =>
  (Object.entries(configRoles.value) as Array<[ 'conversion' | 'layering' | 'receive', boolean ]>)
    .filter(([, v]) => v)
    .map(([k]) => k),
);

async function refresh() {
  try {
    rows.value = (await workstationsApi.list()).workstations;
  } finally {
    loading.value = false;
  }
}

async function refreshAgentInfo() {
  agentInfoError.value = null;
  try {
    agentInfo.value = await workstationsApi.agentInfo();
  } catch (err) {
    agentInfoError.value = (err as ApiError).message ?? 'failed to load agent info';
  }
}

async function previewConfig() {
  previewError.value = null;
  copied.value = false;
  if (!configNodeName.value.trim()) {
    previewError.value = 'node name is required';
    return;
  }
  try {
    previewedConfig.value = await workstationsApi.fetchAgentConfig({
      nodeName: configNodeName.value.trim(),
      slots: configSlots.value,
      roles: selectedRoles.value,
    });
  } catch (err) {
    previewError.value = (err as ApiError).message ?? 'failed';
  }
}

function downloadConfig() {
  if (!previewedConfig.value) return;
  const url = workstationsApi.agentConfigUrl({
    nodeName: previewedConfig.value.nodeName,
    slots: previewedConfig.value.slots,
    roles: previewedConfig.value.roles,
  });
  // Use anchor click rather than location.href so the cookie-authed
  // fetch goes through with the right Content-Disposition.
  const a = document.createElement('a');
  a.href = url;
  a.click();
}

async function copyConfig() {
  if (!previewedConfigText.value) return;
  await navigator.clipboard.writeText(previewedConfigText.value);
  copied.value = true;
  setTimeout(() => (copied.value = false), 1500);
}

async function toggleEnabled(w: Workstation) {
  const updated = await workstationsApi.update(w.id, { isEnabled: !w.isEnabled });
  Object.assign(w, updated);
}

type RoleField = 'canConvert' | 'canLayer' | 'canReceive';

async function toggleRole(w: Workstation, role: RoleField) {
  const key = `${w.id}:${role}`;
  if (inFlightRoleEdits.has(key)) return;
  inFlightRoleEdits.add(key);
  try {
    const updated = await workstationsApi.update(w.id, { [role]: !w[role] } as Partial<Workstation>);
    // Server returns the fresh row but does not include live join fields
    // (`online`, `slotsBusy`, `sessions`). Preserve them from the local copy.
    const live = { online: w.online, slotsBusy: w.slotsBusy, sessions: w.sessions };
    Object.assign(w, updated, live);
  } catch (err) {
    // Surface the failure so the UI doesn't silently swallow it; we keep the
    // pill in its previous (unchanged) state because we never optimistically
    // mutated `w` above.
    const msg = (err as ApiError)?.message ?? 'role update failed';
    console.error('[Workstations] failed to toggle role', { workstation: w.nodeName, role, error: msg });
    alert(`Failed to toggle ${role.replace('can', '').toLowerCase()} for ${w.nodeName}:\n${msg}`);
  } finally {
    inFlightRoleEdits.delete(key);
  }
}

function isRoleBusy(w: Workstation, role: RoleField): boolean {
  return inFlightRoleEdits.has(`${w.id}:${role}`);
}

async function remove(w: Workstation) {
  if (!confirm(`Delete workstation "${w.nodeName}"?`)) return;
  await workstationsApi.remove(w.id);
  await refresh();
}

// ---------------------------------------------------------------- version compare
/** Splits a semver-ish string into numeric parts; non-numeric tails are stripped.
 *  Accepts `v0.1.13`, `0.1.13`, `0.1.13-rc.2`; returns `[0, 1, 13]` for all. */
function parseSemver(v: string): number[] {
  const cleaned = v.replace(/^v/, '').split(/[-+]/, 1)[0] ?? '';
  return cleaned.split('.').map((s) => {
    const n = parseInt(s, 10);
    return Number.isFinite(n) ? n : 0;
  });
}

/** Returns -1 if a<b, 0 if equal, 1 if a>b. */
function compareSemver(a: string, b: string): number {
  const pa = parseSemver(a);
  const pb = parseSemver(b);
  const len = Math.max(pa.length, pb.length);
  for (let i = 0; i < len; i++) {
    const ai = pa[i] ?? 0;
    const bi = pb[i] ?? 0;
    if (ai < bi) return -1;
    if (ai > bi) return 1;
  }
  return 0;
}

type AgentVersionState = 'unknown' | 'outdated' | 'latest';

function agentVersionState(w: Workstation): AgentVersionState {
  const ws = w.agentVersion?.trim();
  const latest = agentInfo.value?.version?.trim();
  if (!ws) return 'unknown';
  if (!latest) return 'unknown';
  return compareSemver(ws, latest) < 0 ? 'outdated' : 'latest';
}

onMounted(async () => {
  await Promise.all([refresh(), refreshAgentInfo()]);

  // 1) Live updates: refresh whenever the WS reports a workstation event.
  //    Matches the Dashboard pattern (see Dashboard.vue).
  unsubscribeWs = adminWs.on((ev) => {
    if (ev.type === 'workstation') {
      void refresh();
    }
  });

  // 2) Belt-and-braces fallback: poll every 5s while mounted so that missed
  //    WS events still surface within a tick.
  pollTimer = setInterval(() => { void refresh(); }, 5_000);
});

onUnmounted(() => {
  unsubscribeWs?.();
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
});
</script>

<template>
  <h1>Workstations</h1>
  <p class="muted">Agent connections register themselves on first connect. Toggle enabled to gate dispatch.</p>

  <!-- ============================================================ DOWNLOADS -->
  <section class="block">
    <header class="block-head">
      <h2>Node downloads</h2>
      <span v-if="agentInfo?.version" class="pill online">{{ agentInfo.version }}</span>
    </header>
    <p class="muted small">
      Everything a fresh Rhino host needs to join the pool. Download the agent payload,
      drop the generated <code>agent-config.json</code> next to <code>PRISM.Agent.exe</code>,
      and run the installer in an elevated PowerShell.
    </p>

    <div v-if="agentInfoError" class="error-box mt">{{ agentInfoError }}</div>

    <div class="downloads-grid mt">
      <!-- Step 1: agent payload -->
      <div class="card download-card">
        <div class="step-num">1</div>
        <h3>Agent payload</h3>
        <p class="muted small">
          Self-contained <code>PRISM.Agent.exe</code> + dependencies, zipped by the
          <code>agent-msi</code> GitHub Action.
        </p>
        <div v-if="agentInfo?.available" class="mt-sm">
          <a :href="workstationsApi.agentDownloadUrl()" class="btn primary" download>
            ⇩ Download {{ agentInfo.version ?? 'agent zip' }}
          </a>
          <div class="muted mono mt-sm" style="font-size: 11px; word-break: break-all;">
            {{ agentInfo.downloadUrl }}
          </div>
        </div>
        <div v-else-if="agentInfo" class="info-box mt-sm">
          <strong>Build pending.</strong> No agent zip is registered yet.
          Trigger <code>{{ agentInfo.buildSource.workflow }}</code> (push a
          <code>v*</code> tag or run it manually from the Actions tab) and paste the
          resulting URL into <RouterLink :to="{ name: 'settings' }">Settings</RouterLink>
          under <code>workstation_agent_download_url</code>.
        </div>
        <div v-else class="muted small mt-sm">loading…</div>
      </div>

      <!-- Step 2: config template -->
      <div class="card download-card">
        <div class="step-num">2</div>
        <h3>Per-node <code>agent-config.json</code></h3>
        <p class="muted small">
          Generated server-side with the right WSS endpoint baked in.
          <span v-if="agentInfo?.wsUrl">Currently: <code class="mono">{{ agentInfo.wsUrl }}</code></span>
        </p>

        <div class="config-form mt-sm">
          <label class="config-label">
            Node name
            <input v-model="configNodeName" placeholder="RB-DA2-PC02" />
          </label>
          <label class="config-label" style="width: 90px;">
            Slots
            <input v-model.number="configSlots" type="number" min="1" max="16" />
          </label>
        </div>
        <div class="role-row mt-sm">
          <label><input type="checkbox" v-model="configRoles.conversion" /> conversion</label>
          <label><input type="checkbox" v-model="configRoles.layering" />   layering</label>
          <label><input type="checkbox" v-model="configRoles.receive" />    receive</label>
        </div>
        <div class="mt-sm h-row gap-sm" style="flex-wrap: wrap;">
          <button class="primary" :disabled="!configNodeName" @click="previewConfig">Generate</button>
          <button :disabled="!previewedConfig" @click="downloadConfig">⇩ Download .json</button>
          <button :disabled="!previewedConfig" @click="copyConfig">{{ copied ? 'Copied!' : 'Copy' }}</button>
        </div>
        <div v-if="previewError" class="error-box mt-sm">{{ previewError }}</div>
        <pre v-if="previewedConfig" class="config-preview mt-sm">{{ previewedConfigText }}</pre>
      </div>

      <!-- Step 3: install scripts -->
      <div class="card download-card">
        <div class="step-num">3</div>
        <h3>Install scripts</h3>
        <p class="muted small">
          PowerShell helpers shipped inside the agent zip, also available standalone here so you can
          re-install on a node without re-downloading the whole payload.
        </p>
        <div class="mt-sm h-row gap-sm" style="flex-wrap: wrap;">
          <a :href="workstationsApi.installScriptUrl('install')" class="btn" download>⇩ install.ps1</a>
          <a :href="workstationsApi.installScriptUrl('uninstall')" class="btn" download>⇩ uninstall.ps1</a>
        </div>
      </div>
    </div>

    <!-- Step 4: instructions -->
    <div class="card mt">
      <h3 style="margin-top: 0;">Install on the workstation</h3>
      <ol class="instructions">
        <li>Copy the agent zip and the generated <code>agent-config.json</code> to the new Rhino host (any temp folder).</li>
        <li>Open an <strong>elevated</strong> PowerShell, unblock, and extract:
<pre>Unblock-File .\PRISM.Agent-{{ agentInfo?.version ?? 'vX.Y.Z' }}.zip
Expand-Archive .\PRISM.Agent-{{ agentInfo?.version ?? 'vX.Y.Z' }}.zip -DestinationPath .\PRISM.Agent
cd .\PRISM.Agent</pre>
        </li>
        <li>Drop the generated config next to the payload (so the service finds it):
<pre>Copy-Item .\agent-config-&lt;node&gt;.json .\agent-config.json</pre>
        </li>
        <li>Run the installer (writes <code>C:\Program Files\PRISM.Agent\</code>, registers + starts the Windows service):
<pre>.\install.ps1 `
    -PrismUrl '{{ agentInfo?.wsUrl ?? 'wss://prism.rebus.industries/ws/agent' }}' `
    -NodeName $env:COMPUTERNAME `
    -Slots 2</pre>
        </li>
        <li>Verify — the row should appear above as <span class="pill online">online</span> within a few seconds:
<pre>Get-Service PRISM.Agent
Get-Content C:\ProgramData\PRISM.Agent\logs\*.log -Tail 20 -Wait</pre>
        </li>
      </ol>
      <p class="muted small">
        More detail: <a href="https://github.com/REBUS-ORBIT/prism/blob/main/AGENT_INSTALL.md" target="_blank" rel="noopener">AGENT_INSTALL.md</a>.
      </p>
    </div>
  </section>

  <!-- ============================================================ POOL TABLE -->
  <section class="block">
    <header class="block-head">
      <h2>Registered workstations</h2>
    </header>
    <div class="card">
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
              <div class="role-pills">
                <button
                  type="button"
                  class="pill role-pill"
                  :class="[w.canConvert ? 'convert-on' : 'role-off', { 'role-busy': isRoleBusy(w, 'canConvert') }]"
                  :disabled="isRoleBusy(w, 'canConvert')"
                  :title="`Click to ${w.canConvert ? 'disable' : 'enable'} convert`"
                  @click="toggleRole(w, 'canConvert')"
                >convert</button>
                <button
                  type="button"
                  class="pill role-pill"
                  :class="[w.canLayer ? 'layer-on' : 'role-off', { 'role-busy': isRoleBusy(w, 'canLayer') }]"
                  :disabled="isRoleBusy(w, 'canLayer')"
                  :title="`Click to ${w.canLayer ? 'disable' : 'enable'} layer`"
                  @click="toggleRole(w, 'canLayer')"
                >layer</button>
                <button
                  type="button"
                  class="pill role-pill"
                  :class="[w.canReceive ? 'receive-on' : 'role-off', { 'role-busy': isRoleBusy(w, 'canReceive') }]"
                  :disabled="isRoleBusy(w, 'canReceive')"
                  :title="`Click to ${w.canReceive ? 'disable' : 'enable'} receive`"
                  @click="toggleRole(w, 'canReceive')"
                >receive</button>
              </div>
            </td>
            <td>
              <div class="agent-cell">
                <span class="muted">{{ w.agentVersion ?? '—' }}</span>
                <span
                  v-if="agentVersionState(w) === 'outdated'"
                  class="pill version-pill version-outdated"
                  :title="`Latest is ${agentInfo?.version}`"
                >update available</span>
                <span
                  v-else-if="agentVersionState(w) === 'latest' && w.agentVersion"
                  class="pill version-pill version-latest"
                  title="Running latest"
                >latest</span>
                <span
                  v-else-if="agentVersionState(w) === 'unknown'"
                  class="pill version-pill version-unknown"
                  :title="agentInfo?.version ? 'Agent has not reported a version yet' : 'Latest agent version is unknown'"
                >unknown</span>
              </div>
            </td>
            <td class="muted">{{ w.rhinoVersion ?? '—' }}</td>
            <td class="muted">{{ w.lastSeenAt ? new Date(w.lastSeenAt).toLocaleString() : '—' }}</td>
            <td>
              <button @click="toggleEnabled(w)">{{ w.isEnabled ? 'Disable' : 'Enable' }}</button>
              <button @click="remove(w)" style="margin-left: 4px;">Delete</button>
            </td>
          </tr>
          <tr v-if="!rows.length">
            <td colspan="9" class="muted" style="text-align: center; padding: 32px;">
              No workstations registered. Use the Node downloads above to install PRISM.Agent on a Rhino host.
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else class="muted">loading…</div>
    </div>
  </section>
</template>

<style scoped>
h1 { font-size: 22px; margin: 0 0 8px; }
h2 { font-size: 14px; margin: 0; letter-spacing: 0.04em; text-transform: uppercase; color: var(--color-text-muted); }
h3 { font-size: 14px; margin: 0 0 4px; }
.small { font-size: 12px; }

.block { margin-top: 28px; }
.block-head {
  display: flex; align-items: center; justify-content: space-between;
  gap: 12px; margin-bottom: 8px;
}

.downloads-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
  gap: 12px;
}

.download-card {
  position: relative;
  padding-top: 28px;
}

.step-num {
  position: absolute;
  top: 12px; right: 12px;
  width: 24px; height: 24px;
  display: flex; align-items: center; justify-content: center;
  background: var(--orbit-primary-fade);
  color: var(--orbit-primary);
  border-radius: 999px;
  font-weight: 700;
  font-size: 12px;
}

.config-form {
  display: flex; gap: 10px; align-items: flex-end; flex-wrap: wrap;
}
.config-label {
  display: flex; flex-direction: column; gap: 4px;
  font-size: 12px; color: var(--color-text-muted);
  flex: 1;
}
.config-label input { width: 100%; }

.role-row {
  display: flex; gap: 12px; flex-wrap: wrap;
  font-size: 13px;
}
.role-row label { display: flex; align-items: center; gap: 4px; cursor: pointer; }

.config-preview {
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  padding: 8px 10px;
  margin: 0;
  font-family: var(--font-mono);
  font-size: 11px;
  max-height: 220px;
  overflow: auto;
  white-space: pre;
}

.instructions {
  margin: 8px 0 0; padding-left: 20px;
  display: flex; flex-direction: column; gap: 10px;
  font-size: 13px;
}
.instructions pre {
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius);
  padding: 8px 10px;
  margin: 6px 0 0;
  font-family: var(--font-mono);
  font-size: 12px;
  overflow-x: auto;
  white-space: pre;
}

a.btn { display: inline-block; text-decoration: none; }

/* ------------------------------------------------------------ role pills */
.role-pills {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 4px;
}

/* Override the generic <button> styling for pill-shaped role toggles. */
button.role-pill {
  appearance: none;
  border: 1px solid transparent;
  padding: 2px 8px;
  font-size: 11px;
  font-weight: 600;
  font-family: inherit;
  letter-spacing: 0.02em;
  text-transform: uppercase;
  cursor: pointer;
  border-radius: 999px;
  transition: opacity 120ms ease-out, background-color 120ms ease-out, color 120ms ease-out;
}
button.role-pill:hover { border-color: var(--color-border-strong); }
button.role-pill:disabled { cursor: progress; }

button.role-pill.convert-on {
  background: var(--color-success-bg);
  color: var(--color-success);
}
button.role-pill.layer-on {
  background: var(--color-info-bg);
  color: var(--color-info);
}
button.role-pill.receive-on {
  background: var(--color-warn-bg);
  color: var(--color-warn);
}
button.role-pill.role-off {
  background: var(--color-bg-hover);
  color: var(--color-text-subtle);
}
button.role-pill.role-busy {
  opacity: 0.5;
}

/* ------------------------------------------------------------ agent column */
.agent-cell {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.version-pill {
  font-size: 10px;
  letter-spacing: 0.03em;
}
.version-outdated {
  background: var(--color-warn-bg);
  color: var(--color-warn);
}
.version-latest {
  background: var(--color-success-bg);
  color: var(--color-success);
}
.version-unknown {
  background: var(--color-bg-hover);
  color: var(--color-text-subtle);
}
</style>
