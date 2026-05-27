<script setup lang="ts">
import { onMounted, onUnmounted, reactive, ref } from 'vue';
import { RouterLink } from 'vue-router';
import {
  workstationsApi,
  type AgentBuildInfo,
  type ApiError,
  type Workstation,
} from '../../shared/api';
import { adminWs } from '../../shared/ws';

const rows = ref<Workstation[]>([]);
const loading = ref(true);

// Tracks in-flight role toggles as a `${workstationId}:${role}` set so we can
// dim the pill while the PATCH is in flight and ignore double-clicks.
const inFlightRoleEdits = reactive(new Set<string>());

// Tracks in-flight restart/update dispatches so we can disable buttons + show
// a spinner, keyed `${workstationId}:${'restart'|'update'}`.
const inFlightLifecycle = reactive(new Set<string>());

// Transient per-row status message shown next to the Restart/Update buttons
// for ~3s after a click. Keyed by workstation id; both ok + error variants
// flow through the same map (the colour comes from `kind`).
interface LifecycleStatus { kind: 'ok' | 'err'; msg: string }
const lifecycleStatus = reactive(new Map<string, LifecycleStatus>());
// setTimeout handles per workstation so a second action cancels the previous
// auto-clear instead of stomping a fresher message.
const lifecycleTimers = new Map<string, ReturnType<typeof setTimeout>>();

// Default port for the agent's local web UI (since v0.1.31; bindAll defaults
// to true so the LAN can reach it). `webUiPort` is not surfaced via the
// workstations API yet, so we hard-code 7421 here -- it matches every
// install we control. See AGENT_INSTALL.md.
const AGENT_WEB_UI_PORT = 7421;

let unsubscribeWs: (() => void) | null = null;
let pollTimer: ReturnType<typeof setInterval> | null = null;

// ---------------------------------------------------------------- downloads
const agentInfo = ref<AgentBuildInfo | null>(null);
const agentInfoError = ref<string | null>(null);

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

// ---------------------------------------------------------------- lifecycle
/** Best-effort hostname for the agent's local web UI link. The DB doesn't
 *  carry a dedicated host column yet -- `nodeName` is the only stable
 *  identifier the row exposes, so we lean on the LAN to resolve it. Works
 *  out-of-the-box on AD-joined networks and any LAN with mDNS or a hosts
 *  file entry; manual IP overrides are a server-side TODO. */
function webUiHost(w: Workstation): string {
  // `nodeName` is required and length-limited (varchar(128)); strip
  // whitespace defensively so the URL stays well-formed.
  return w.nodeName.trim();
}

function webUiUrl(w: Workstation): string {
  return `http://${webUiHost(w)}:${AGENT_WEB_UI_PORT}/`;
}

function setLifecycleStatus(workstationId: string, status: LifecycleStatus): void {
  lifecycleStatus.set(workstationId, status);
  const prev = lifecycleTimers.get(workstationId);
  if (prev) clearTimeout(prev);
  const timer = setTimeout(() => {
    lifecycleStatus.delete(workstationId);
    lifecycleTimers.delete(workstationId);
  }, 3_000);
  lifecycleTimers.set(workstationId, timer);
}

function lifecycleErrorMessage(err: unknown, action: 'restart' | 'update'): string {
  const e = err as ApiError | undefined;
  if (e?.status === 503) return 'Agent offline — try again when it reconnects.';
  if (e?.status === 404) return 'Workstation not found.';
  return e?.message ?? `${action} failed`;
}

function isLifecycleBusy(w: Workstation, action: 'restart' | 'update'): boolean {
  return inFlightLifecycle.has(`${w.id}:${action}`);
}

async function restartAgent(w: Workstation) {
  if (isLifecycleBusy(w, 'restart')) return;
  const ok = confirm(
    `Restart agent on ${w.nodeName}?\n\nAny in-flight conversion will be aborted.`,
  );
  if (!ok) return;
  const key = `${w.id}:restart`;
  inFlightLifecycle.add(key);
  try {
    await workstationsApi.restart(w.id);
    setLifecycleStatus(w.id, { kind: 'ok', msg: 'Restart queued' });
  } catch (err) {
    setLifecycleStatus(w.id, { kind: 'err', msg: lifecycleErrorMessage(err, 'restart') });
  } finally {
    inFlightLifecycle.delete(key);
  }
}

async function updateAgentBuild(w: Workstation) {
  if (isLifecycleBusy(w, 'update')) return;
  const ok = confirm(
    `Update agent on ${w.nodeName} to the latest release?\n\nThe agent will restart itself when the update completes.`,
  );
  if (!ok) return;
  const key = `${w.id}:update`;
  inFlightLifecycle.add(key);
  try {
    await workstationsApi.updateAgent(w.id);
    setLifecycleStatus(w.id, { kind: 'ok', msg: 'Update queued' });
  } catch (err) {
    setLifecycleStatus(w.id, { kind: 'err', msg: lifecycleErrorMessage(err, 'update') });
  } finally {
    inFlightLifecycle.delete(key);
  }
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
  for (const t of lifecycleTimers.values()) clearTimeout(t);
  lifecycleTimers.clear();
});
</script>

<template>
  <h1>Workstations</h1>
  <p class="muted">Agent connections register themselves on first connect. Toggle enabled to gate dispatch.</p>

  <!-- ============================================================ DOWNLOAD -->
  <section class="block">
    <header class="block-head">
      <h2>Agent installer</h2>
      <span v-if="agentInfo?.version" class="pill online">{{ agentInfo.version }}</span>
    </header>
    <p class="muted small">
      Latest PRISM.Agent wizard installer (built by the
      <code>agent-msi</code> GitHub Action and attached to the
      <code>REBUS-ORBIT/prism-agent</code> release).
    </p>

    <div v-if="agentInfoError" class="error-box mt">{{ agentInfoError }}</div>

    <div class="card download-card mt">
      <div v-if="agentInfo?.available">
        <div class="h-row gap-sm" style="flex-wrap: wrap; align-items: center;">
          <a :href="workstationsApi.agentDownloadUrl()" class="btn primary" download>
            ⇩ Download {{ agentInfo.version ?? 'installer' }}
          </a>
          <a
            :href="workstationsApi.releasesPageUrl"
            class="btn"
            target="_blank"
            rel="noopener noreferrer"
          >View on GitHub ↗</a>
        </div>
        <p class="muted small install-note">
          Run the downloaded <code>.exe</code> on each workstation as
          <strong>administrator</strong>. The wizard handles everything —
          server URL, node name, slot count, scheduled task, log dir. See
          <a
            href="https://github.com/REBUS-ORBIT/prism/blob/main/AGENT_INSTALL.md"
            target="_blank"
            rel="noopener noreferrer"
          >AGENT_INSTALL.md</a> for details.
        </p>
      </div>
      <div v-else-if="agentInfo" class="info-box">
        <strong>Build pending.</strong> No agent installer is registered yet.
        Trigger <code>{{ agentInfo.buildSource.workflow }}</code> (push a
        <code>v*</code> tag or run it manually from the Actions tab) and the
        latest release will surface here automatically. To pin a specific
        version, set <code>workstation_agent_download_url</code> /
        <code>workstation_agent_version</code> in
        <RouterLink :to="{ name: 'settings' }">Settings</RouterLink>.
      </div>
      <div v-else class="muted small">loading…</div>
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
              <div class="row-actions">
                <a
                  :href="webUiUrl(w)"
                  target="_blank"
                  rel="noopener noreferrer"
                  class="btn btn-small"
                  :title="`Opens ${webUiUrl(w)} in a new tab — requires LAN DNS for ${webUiHost(w)}.`"
                >Open Web UI ↗</a>
                <button
                  class="btn-small"
                  :disabled="!w.online || isLifecycleBusy(w, 'restart')"
                  :title="w.online ? `Restart agent on ${w.nodeName}` : 'Agent offline'"
                  @click="restartAgent(w)"
                >Restart</button>
                <button
                  class="primary btn-small"
                  :disabled="!w.online || isLifecycleBusy(w, 'update')"
                  :title="w.online ? `Update agent on ${w.nodeName} to the latest release` : 'Agent offline'"
                  @click="updateAgentBuild(w)"
                >Update</button>
                <button class="btn-small" @click="toggleEnabled(w)">
                  {{ w.isEnabled ? 'Disable' : 'Enable' }}
                </button>
                <button class="btn-small" @click="remove(w)">Delete</button>
              </div>
              <span
                v-if="lifecycleStatus.get(w.id)"
                class="lifecycle-status"
                :class="lifecycleStatus.get(w.id)!.kind === 'ok' ? 'lifecycle-ok' : 'lifecycle-err'"
              >{{ lifecycleStatus.get(w.id)!.msg }}</span>
            </td>
          </tr>
          <tr v-if="!rows.length">
            <td colspan="9" class="muted" style="text-align: center; padding: 32px;">
              No workstations registered. Download the installer above and run it on a Rhino host.
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
.small { font-size: 12px; }

.block { margin-top: 28px; }
.block-head {
  display: flex; align-items: center; justify-content: space-between;
  gap: 12px; margin-bottom: 8px;
}

.download-card {
  padding: 14px 16px;
}
.install-note {
  margin: 12px 0 0;
  line-height: 1.5;
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

/* ------------------------------------------------------------ row actions */
.row-actions {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
}

/* Slightly more compact than the default button so 5 actions still fit a
   single line on a typical admin viewport, but the styles stay close to
   the design-system defaults (no new colours). */
.btn-small {
  padding: 3px 8px;
  font-size: 12px;
}
a.btn-small { text-decoration: none; }

.lifecycle-status {
  display: inline-block;
  margin-top: 4px;
  margin-left: 2px;
  font-size: 11px;
  line-height: 1.4;
}
.lifecycle-ok  { color: var(--color-success); }
.lifecycle-err { color: var(--color-error); }
</style>
