namespace PRISM.Agent.WebUi;

/// <summary>
/// Single-file HTML page served at <c>GET /</c>.  Vanilla CSS + JS so the
/// agent's self-contained publish does not need a frontend build step.
///
/// Styling mirrors <c>PRISM/web/src/shared/designSystem.css</c> -- ORBIT
/// orange brand (<c>#e06238</c>), dark + light themes via
/// <c>[data-theme="dark"]</c> on <c>html</c>, neutral palette sampled from
/// the live ORBIT site.  The user's theme choice is persisted under the
/// same <c>prism.theme</c> localStorage key the SPA uses.
/// </summary>
internal static class IndexHtml
{
    public const string Body = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>PRISM Agent</title>
<script>
  (function () {
    var saved = localStorage.getItem('prism.theme') || 'system';
    var dark = saved === 'dark' ||
      (saved === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  })();
</script>
<style>
  /* -- ORBIT/PRISM design tokens (mirrors web/src/shared/designSystem.css) -- */
  :root {
    color-scheme: light;
    --orbit-primary:        #e06238;
    --orbit-primary-hover:  #c4542d;
    --orbit-primary-fade:   #fde9df;
    --color-bg:             #ffffff;
    --color-bg-elevated:    #fafafa;
    --color-bg-input:       #ffffff;
    --color-bg-hover:       #f3f4f6;
    --color-border:         #e5e7eb;
    --color-border-strong:  #d1d5db;
    --color-text:           #111827;
    --color-text-muted:     #4b5563;
    --color-text-subtle:    #9ca3af;
    --color-success:        #15803d;
    --color-success-bg:     #dcfce7;
    --color-warn:           #b45309;
    --color-warn-bg:        #fef3c7;
    --color-error:          #b91c1c;
    --color-error-bg:       #fee2e2;
    --color-info:           #1d4ed8;
    --color-info-bg:        #dbeafe;
    --radius-sm: 4px;
    --radius:    8px;
    --radius-lg: 12px;
    --shadow-1: 0 1px 2px 0 rgba(0,0,0,.05);
    --shadow-2: 0 4px 12px -2px rgba(0,0,0,.08), 0 2px 4px -1px rgba(0,0,0,.04);
    --font-sans: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Inter, "Helvetica Neue", Arial, sans-serif;
    --font-mono: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
  }
  [data-theme="dark"] {
    color-scheme: dark;
    --orbit-primary-hover:  #c94f26;
    --orbit-primary-fade:   #382d29;
    --color-bg:             #101012;
    --color-bg-elevated:    #15161c;
    --color-bg-input:       #191a22;
    --color-bg-hover:       #1f2129;
    --color-border:         #332b28;
    --color-border-strong:  #3e312d;
    --color-text:           #ffffff;
    --color-text-muted:     #b0b1b5;
    --color-text-subtle:    #7e7f82;
    --color-success:        #34d399;
    --color-success-bg:     #072c1f;
    --color-warn:           #fbbf24;
    --color-warn-bg:        #302303;
    --color-error:          #f87171;
    --color-error-bg:       #300303;
    --color-info:           #93c5fd;
    --color-info-bg:        #1e2a3d;
    --shadow-1: 0 1px 2px 0 rgba(0,0,0,.4);
    --shadow-2: 0 4px 12px -2px rgba(0,0,0,.55), 0 2px 4px -1px rgba(0,0,0,.4);
  }

  * { box-sizing: border-box; }
  html, body { margin: 0; padding: 0; height: 100%; }
  body {
    background: var(--color-bg);
    color: var(--color-text);
    font: 14px/1.5 var(--font-sans);
  }

  a { color: var(--orbit-primary); text-decoration: none; }
  a:hover { text-decoration: underline; }

  /* Header */
  header {
    display: flex; align-items: center; justify-content: space-between;
    padding: 14px 24px;
    background: var(--color-bg-elevated);
    border-bottom: 1px solid var(--color-border);
    position: sticky; top: 0; z-index: 5;
  }
  header .title {
    display: flex; align-items: center; gap: 12px;
    font-size: 16px; font-weight: 600; letter-spacing: .2px; margin: 0;
  }
  header .title .logo {
    width: 22px; height: 22px;
    border-radius: 6px;
    background: var(--orbit-primary);
    display: inline-flex; align-items: center; justify-content: center;
    color: #fff; font-weight: 700; font-size: 13px;
  }
  header .title .dot {
    width: 8px; height: 8px; border-radius: 50%;
    background: var(--color-warn);
    box-shadow: 0 0 8px currentColor;
  }
  header.connected .dot { background: var(--color-success); }
  header.paused    .dot { background: var(--color-text-subtle); box-shadow: none; }
  header.offline   .dot { background: var(--color-error); }

  header .meta { display: flex; gap: 14px; align-items: center; color: var(--color-text-muted); font-size: 12px; }
  header .meta code { font-family: var(--font-mono); font-size: 12px; background: var(--color-bg-input); padding: 2px 6px; border-radius: 4px; }

  /* Layout */
  main { max-width: 1080px; margin: 0 auto; padding: 24px 24px 80px; }

  .card {
    background: var(--color-bg-elevated);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    box-shadow: var(--shadow-1);
    margin-bottom: 18px;
    overflow: hidden;
  }
  .card > h2 {
    margin: 0; padding: 12px 18px;
    font-size: 11px; font-weight: 600;
    text-transform: uppercase; letter-spacing: .12em;
    color: var(--color-text-muted);
    border-bottom: 1px solid var(--color-border);
    background: var(--color-bg);
  }
  .card .body { padding: 18px; }

  /* Stats grid */
  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px,1fr)); gap: 12px; }
  .stat {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: var(--radius);
    padding: 14px 16px;
  }
  .stat .label {
    color: var(--color-text-muted);
    font-size: 11px; text-transform: uppercase; letter-spacing: .08em;
    margin-bottom: 6px;
  }
  .stat .value { font-size: 18px; font-weight: 600; }

  /* Forms */
  .row { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px,1fr)); gap: 14px; }
  label.field { display: block; }
  label.field > span {
    display: block;
    color: var(--color-text-muted); font-size: 11px;
    text-transform: uppercase; letter-spacing: .08em;
    margin-bottom: 6px;
  }

  input, select, textarea {
    width: 100%;
    background: var(--color-bg-input);
    color: var(--color-text);
    border: 1px solid var(--color-border-strong);
    border-radius: var(--radius);
    padding: 7px 10px;
    font: 13px/1.4 var(--font-sans);
  }
  input:focus, select:focus { outline: none; border-color: var(--orbit-primary); box-shadow: 0 0 0 3px var(--orbit-primary-fade); }

  /* Toggle group */
  .toggle-row { display: flex; gap: 10px; flex-wrap: wrap; }
  .toggle {
    display: inline-flex; align-items: center; gap: 8px;
    background: var(--color-bg);
    padding: 7px 12px;
    border-radius: var(--radius);
    cursor: pointer;
    border: 1px solid var(--color-border);
    user-select: none;
    font-size: 13px;
  }
  .toggle input { width: auto; accent-color: var(--orbit-primary); }
  .toggle.checked {
    border-color: var(--orbit-primary);
    background: var(--orbit-primary-fade);
    color: var(--orbit-primary);
  }
  [data-theme="dark"] .toggle.checked { color: var(--color-text); }

  /* Buttons */
  .actions { display: flex; gap: 10px; flex-wrap: wrap; margin-top: 16px; }
  button {
    appearance: none;
    border: 1px solid var(--color-border-strong);
    background: var(--color-bg-elevated);
    color: var(--color-text);
    border-radius: var(--radius);
    padding: 7px 14px;
    font: 500 13px var(--font-sans);
    cursor: pointer;
  }
  button:hover { border-color: var(--orbit-primary); }
  button.primary {
    background: var(--orbit-primary); border-color: var(--orbit-primary); color: #fff;
  }
  button.primary:hover { background: var(--orbit-primary-hover); border-color: var(--orbit-primary-hover); }
  button.danger {
    background: var(--color-error-bg); border-color: var(--color-error); color: var(--color-error);
  }
  button.danger:hover { background: var(--color-error); color: #fff; }
  button:disabled { opacity: .45; cursor: not-allowed; }

  button.icon-btn {
    background: transparent;
    border: 1px solid transparent;
    color: var(--color-text-muted);
    padding: 6px;
    width: 32px; height: 32px;
    display: inline-flex; align-items: center; justify-content: center;
  }
  button.icon-btn:hover { background: var(--color-bg-hover); border-color: var(--color-border); color: var(--color-text); }

  /* Pills */
  .pill {
    display: inline-flex; align-items: center;
    padding: 2px 8px; border-radius: 999px;
    font-size: 11px; font-weight: 600; letter-spacing: 0.02em;
    text-transform: uppercase;
  }
  .pill.online { background: var(--color-success-bg); color: var(--color-success); }
  .pill.offline { background: var(--color-error-bg); color: var(--color-error); }
  .pill.paused { background: var(--color-warn-bg);  color: var(--color-warn); }

  /* Formats list */
  .formats { display: flex; gap: 6px; flex-wrap: wrap; }
  .formats code {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    padding: 3px 8px; border-radius: var(--radius-sm);
    font: 12px var(--font-mono); color: var(--color-text);
  }

  /* Logs */
  pre.logs {
    background: var(--color-bg-input);
    color: var(--color-text);
    border: 1px solid var(--color-border);
    border-radius: var(--radius);
    padding: 12px;
    height: 360px; overflow: auto; margin: 0;
    font: 12px/1.5 var(--font-mono);
  }

  /* Hint / box */
  .hint { color: var(--color-text-muted); font-size: 12px; margin-top: 8px; }
  .info-box {
    background: var(--color-info-bg); color: var(--color-info);
    padding: 8px 12px; border-radius: var(--radius);
    font-size: 13px; margin-bottom: 12px;
  }
  .warn-box {
    background: var(--color-warn-bg); color: var(--color-warn);
    padding: 8px 12px; border-radius: var(--radius);
    font-size: 13px; margin-bottom: 12px;
  }

  /* Toast */
  .toast {
    position: fixed; bottom: 24px; right: 24px;
    background: var(--color-bg-elevated);
    border: 1px solid var(--color-border);
    border-radius: var(--radius);
    padding: 12px 18px;
    box-shadow: var(--shadow-2);
    color: var(--color-text);
    font-size: 13px;
    transform: translateY(20px); opacity: 0; pointer-events: none;
    transition: transform .2s ease, opacity .2s ease;
    z-index: 50;
    max-width: 360px;
  }
  .toast.show { transform: translateY(0); opacity: 1; }
  .toast.error { border-color: var(--color-error); color: var(--color-error); }
  .toast.warn  { border-color: var(--color-warn);  color: var(--color-warn); }
  .toast.success { border-color: var(--color-success); color: var(--color-success); }
</style>
</head>
<body>

<header id="header" class="offline">
  <h1 class="title">
    <span class="logo">P</span>
    <span class="dot"></span>
    PRISM Agent
  </h1>
  <div class="meta">
    <span><code id="version">—</code></span>
    <span><code id="machineId">—</code></span>
    <button class="icon-btn" id="themeToggle" title="Toggle theme" aria-label="Toggle theme">
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79Z"></path>
      </svg>
    </button>
  </div>
</header>

<main>

  <section class="card">
    <h2>Status</h2>
    <div class="body">
      <div class="grid">
        <div class="stat"><div class="label">Connection</div><div class="value" id="connState">—</div></div>
        <div class="stat"><div class="label">Watcher</div><div class="value" id="watcherState">—</div></div>
        <div class="stat"><div class="label">Slots busy</div><div class="value" id="slotsBusy">—</div></div>
        <div class="stat"><div class="label">Slots total</div><div class="value" id="slotsTotal">—</div></div>
      </div>
      <div class="actions">
        <button id="btnPause" class="danger">Pause watcher</button>
        <button id="btnResume" class="primary">Resume watcher</button>
        <button id="btnRefresh">Refresh</button>
      </div>
      <p class="hint">
        Pausing the watcher disconnects this agent from PRISM so new
        conversion / layer / receive jobs route to other workstations.
        Slots already running finish normally.
      </p>
    </div>
  </section>

  <section class="card">
    <h2>Connection</h2>
    <div class="body">
      <div class="row">
        <label class="field">
          <span>PRISM server URL</span>
          <input type="url" id="prismUrl" placeholder="wss://prism.rebus.industries/ws/agent" />
        </label>
        <label class="field">
          <span>Node name</span>
          <input type="text" id="nodeName" />
        </label>
      </div>
      <p class="hint">Restart the agent after changing the server URL.</p>
    </div>
  </section>

  <section class="card">
    <h2>Capacity</h2>
    <div class="body">
      <div class="row">
        <label class="field">
          <span>Slots (1–8)</span>
          <input type="number" id="slots" min="1" max="8" />
        </label>
        <div>
          <span class="hint" style="display:block;margin-bottom:6px;text-transform:uppercase;letter-spacing:.08em;font-size:11px;">Roles</span>
          <div class="toggle-row" id="roles"></div>
        </div>
      </div>
      <p class="hint">
        Slots set parallelism. Rhino is single-instance so jobs serialise on
        the host, but extra slots let the agent accept the next assignment
        while one is finishing its upload phase.
      </p>
    </div>
  </section>

  <section class="card">
    <h2>Rhino &amp; logs</h2>
    <div class="body">
      <div class="row">
        <label class="field">
          <span>Rhino version</span>
          <select id="rhinoVersion">
            <option value="auto">auto (highest installed)</option>
            <option value="8">Rhino 8</option>
            <option value="9">Rhino 9 (when released)</option>
          </select>
        </label>
        <label class="field">
          <span>Log directory</span>
          <input type="text" id="logDir" />
        </label>
      </div>
      <p class="hint">Restart the agent after changing the Rhino version.</p>
    </div>
  </section>

  <section class="card">
    <h2>Web UI access</h2>
    <div class="body">
      <div class="row">
        <label class="field">
          <span>Port</span>
          <input type="number" id="webUiPort" min="0" max="65535" />
        </label>
        <div>
          <span class="hint" style="display:block;margin-bottom:6px;text-transform:uppercase;letter-spacing:.08em;font-size:11px;">Reachable from</span>
          <div class="toggle-row">
            <label class="toggle" id="bindAllLabel">
              <input type="checkbox" id="webUiBindAll" />
              <span>Allow LAN access</span>
            </label>
          </div>
        </div>
      </div>
      <p class="hint">
        With LAN access on, this page is reachable from any other machine on
        the trusted network at <code id="lanUrl">—</code>. The installer
        pre-registers a URL ACL for the configured port so the agent does
        not need to be elevated. Restart required after changing port or
        binding.
      </p>
    </div>
  </section>

  <section class="card">
    <h2>Supported formats</h2>
    <div class="body">
      <div class="formats" id="supportedFormats"></div>
      <p class="hint">
        Extensions advertised in the agent's <code>hello</code> message.
        Formats outside this set get pre-converted upstream by
        <code>prism-assimp</code> before being routed here.
      </p>
    </div>
  </section>

  <section class="card">
    <h2>Save</h2>
    <div class="body">
      <div class="actions">
        <button id="btnSave" class="primary">Save settings</button>
        <button id="btnReload">Discard changes</button>
      </div>
      <p class="hint">
        Live-applied: node name, slots, roles, log dir.
        Restart-required: server URL, Rhino version, web UI port, LAN binding.
      </p>
    </div>
  </section>

  <section class="card">
    <h2>Logs (last 500)</h2>
    <div class="body">
      <pre id="logs" class="logs">loading…</pre>
      <div class="actions">
        <button id="btnLogs">Refresh logs</button>
      </div>
    </div>
  </section>

</main>

<div class="toast" id="toast"></div>

<script>
  const $ = (id) => document.getElementById(id);

  let state = null;
  let dirty = false;

  // ---- Theme toggle ----
  function applyTheme(theme) {
    const dark = theme === 'dark' ||
      (theme === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
  }
  $('themeToggle').addEventListener('click', () => {
    const current = localStorage.getItem('prism.theme') || 'system';
    const next = current === 'dark' ? 'light' : 'dark';
    localStorage.setItem('prism.theme', next);
    applyTheme(next);
  });

  // ---- API helpers ----
  async function api(path, opts = {}) {
    const res = await fetch(path, {
      headers: { 'Content-Type': 'application/json' },
      ...opts,
    });
    if (!res.ok) {
      const text = await res.text().catch(() => '');
      throw new Error(`${res.status} ${res.statusText}: ${text}`);
    }
    return res.json();
  }

  function toast(msg, kind = '') {
    const el = $('toast');
    el.textContent = msg;
    el.className = 'toast show ' + kind;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => { el.className = 'toast'; }, 4000);
  }

  // ---- State rendering ----
  function applyState(s) {
    state = s;

    $('version').textContent = `v${s.agent.version}`;
    $('machineId').textContent = s.agent.machineId.slice(0, 8) + '…';

    const header = $('header');
    header.classList.remove('connected', 'paused', 'offline');
    if (s.agent.paused)         header.classList.add('paused');
    else if (s.agent.connected) header.classList.add('connected');
    else                        header.classList.add('offline');

    $('connState').innerHTML = s.agent.connected
      ? '<span class="pill online">Connected</span>'
      : '<span class="pill offline">Disconnected</span>';
    $('watcherState').innerHTML = s.agent.paused
      ? '<span class="pill paused">Paused</span>'
      : '<span class="pill online">Running</span>';
    $('slotsBusy').textContent = s.agent.slotsBusy;
    $('slotsTotal').textContent = s.config.slots;

    $('btnPause').disabled  = s.agent.paused;
    $('btnResume').disabled = !s.agent.paused;

    if (!dirty) {
      $('prismUrl').value     = s.config.prismUrl;
      $('nodeName').value     = s.config.nodeName;
      $('slots').value        = s.config.slots;
      $('rhinoVersion').value = s.config.rhinoVersion;
      $('logDir').value       = s.config.logDir;
      $('webUiPort').value    = s.config.webUiPort;
      const bindAll = !!s.config.webUiBindAll;
      $('webUiBindAll').checked = bindAll;
      $('bindAllLabel').classList.toggle('checked', bindAll);
      renderRoles(s.availableRoles, new Set(s.config.roles));
    }

    // LAN URL hint -- show the host:port a remote operator would type.
    const host = location.hostname && location.hostname !== 'localhost'
      ? location.hostname
      : '<workstation-ip>';
    $('lanUrl').textContent = `http://${host}:${s.config.webUiPort}/`;

    const formats = $('supportedFormats');
    formats.innerHTML = '';
    for (const ext of s.agent.supportedFormats) {
      const c = document.createElement('code');
      c.textContent = ext;
      formats.appendChild(c);
    }
  }

  function renderRoles(allRoles, enabled) {
    const host = $('roles');
    host.innerHTML = '';
    for (const role of allRoles) {
      const wrap = document.createElement('label');
      wrap.className = 'toggle' + (enabled.has(role) ? ' checked' : '');
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = enabled.has(role);
      cb.dataset.role = role;
      cb.addEventListener('change', () => {
        wrap.classList.toggle('checked', cb.checked);
        markDirty();
      });
      const label = document.createElement('span');
      label.textContent = role;
      wrap.appendChild(cb);
      wrap.appendChild(label);
      host.appendChild(wrap);
    }
  }

  function markDirty() {
    dirty = true;
    $('btnSave').textContent = 'Save settings *';
  }
  function clearDirty() {
    dirty = false;
    $('btnSave').textContent = 'Save settings';
  }

  function collectUpdate() {
    const roles = Array.from(document.querySelectorAll('#roles input:checked'))
      .map((el) => el.dataset.role);
    return {
      prismUrl:     $('prismUrl').value.trim(),
      nodeName:     $('nodeName').value.trim(),
      slots:        Number($('slots').value),
      roles,
      rhinoVersion: $('rhinoVersion').value,
      logDir:       $('logDir').value.trim(),
      webUiPort:    Number($('webUiPort').value),
      webUiBindAll: $('webUiBindAll').checked,
    };
  }

  async function refresh() {
    try {
      const s = await api('/api/state');
      applyState(s);
    } catch (err) {
      toast('Failed to load state: ' + err.message, 'error');
    }
  }

  async function refreshLogs() {
    try {
      const r = await api('/api/logs?n=500');
      $('logs').textContent = r.lines.join('\n') || '(no log lines yet)';
      $('logs').scrollTop = $('logs').scrollHeight;
    } catch (err) {
      $('logs').textContent = 'Failed to load logs: ' + err.message;
    }
  }

  $('btnPause').addEventListener('click', async () => {
    try {
      const r = await api('/api/watcher/pause', { method: 'POST', body: '{}' });
      applyState(r.state);
      toast('Watcher paused', 'warn');
    } catch (err) { toast(err.message, 'error'); }
  });

  $('btnResume').addEventListener('click', async () => {
    try {
      const r = await api('/api/watcher/resume', { method: 'POST', body: '{}' });
      applyState(r.state);
      toast('Watcher resumed', 'success');
    } catch (err) { toast(err.message, 'error'); }
  });

  $('btnRefresh').addEventListener('click', refresh);
  $('btnLogs').addEventListener('click', refreshLogs);
  $('btnReload').addEventListener('click', () => { clearDirty(); refresh(); });

  $('btnSave').addEventListener('click', async () => {
    try {
      const update = collectUpdate();
      const r = await api('/api/config', { method: 'POST', body: JSON.stringify(update) });
      clearDirty();
      applyState(r.state);
      toast(r.restartRequired
        ? 'Saved. Restart the agent to apply server URL / Rhino / web UI changes.'
        : 'Saved.',
        r.restartRequired ? 'warn' : 'success');
    } catch (err) { toast('Save failed: ' + err.message, 'error'); }
  });

  for (const id of ['prismUrl','nodeName','slots','rhinoVersion','logDir','webUiPort']) {
    $(id).addEventListener('input', markDirty);
  }
  $('webUiBindAll').addEventListener('change', () => {
    $('bindAllLabel').classList.toggle('checked', $('webUiBindAll').checked);
    markDirty();
  });

  refresh().then(refreshLogs);
  setInterval(refresh, 4000);
</script>
</body>
</html>
""";
}
