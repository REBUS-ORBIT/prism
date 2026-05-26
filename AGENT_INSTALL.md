# PRISM.Agent — workstation install

PRISM.Agent is the Windows service that runs on each Rhino workstation
in the pool. It connects outbound to the PRISM server over WSS, advertises
its capabilities, and processes conversion / receive jobs against an
in-process Rhino 8 host.

## Prerequisites

- Windows 10 / 11 / Server 2019+ x64
- **Rhino 8** installed and licensed (Zoo or single-user)
- Outbound HTTPS + WSS to `prism.rebus.industries` (port 443)
- An admin PowerShell session

## Install

The release ships **two** download options — pick whichever fits the workflow.

### Option A — Wizard installer (recommended, v0.1.30+)

1. Grab `PRISM.Agent-Setup-vX.Y.Z.exe` from the
   [releases page](https://github.com/REBUS-ORBIT/prism-agent/releases/latest).
2. Right-click → **Run as administrator**.
3. Click through the wizard:
   - **Install location** — defaults to `C:\Program Files\PRISM.Agent`.
   - **PRISM connection settings** — server URL, node name, slot count.
     Defaults are sensible (`wss://prism.rebus.industries/ws/agent`,
     `%COMPUTERNAME%`, 2 slots).
   - **Finish** — optional checkboxes to launch the agent and open the
     local web UI (`http://localhost:7421/`).

The wizard runs `install.ps1` under the hood with the values you typed,
so all the same things happen as Option B (config write, scheduled task,
auto-restart, log dir). Upgrades preserve your existing
`agent-config.json`.

The setup .exe also registers a proper Add/Remove Programs entry, and a
double-click on a newer setup .exe performs an in-place upgrade (the
Inno Setup AppId stays constant across releases).

### Option B — Manual zip + install.ps1 (legacy / unattended deploys)

1. **Download the agent zip** from the
   [releases page](https://github.com/REBUS-ORBIT/prism-agent/releases/latest)
   (file: `PRISM.Agent-vX.Y.Z.zip`).
   The in-app **🔄 Check for Updates** menu item also polls this repo and
   downloads the .zip variant for in-place self-update.
2. **Unblock + extract** to a temp location:

   ```powershell
   Unblock-File .\PRISM.Agent-vX.Y.Z.zip
   Expand-Archive .\PRISM.Agent-vX.Y.Z.zip -DestinationPath .\PRISM.Agent
   cd .\PRISM.Agent
   ```

3. **Run the installer** from an elevated PowerShell:

   ```powershell
   ./install.ps1 `
     -PrismUrl wss://prism.rebus.industries/ws/agent `
     -NodeName $env:COMPUTERNAME `
     -Slots 2 `
     -LaunchNow
   ```

   - `PrismUrl`: the agent WS endpoint (use `ws://10.0.200.211:8765/ws/agent`
     for LAN-direct, bypassing Caddy)
   - `NodeName`: friendly name surfaced in the admin pool
   - `Slots`: how many concurrent conversion jobs this machine handles
     (recommended: number of physical cores ÷ 2, capped at 4)
   - `-LaunchNow`: start the agent immediately after install
   - `-ForceConfig`: overwrite an existing `agent-config.json`
     (otherwise upgrades preserve it)

   The installer:
   - copies the payload to `C:\Program Files\PRISM.Agent\`
   - writes `agent-config.json`
   - registers a Scheduled Task `PRISM.Agent` (At-Logon, RunLevel=Highest)
   - configures retries on failure

## Verify

```powershell
Get-Service PRISM.Agent
Get-Content C:\ProgramData\PRISM.Agent\logs\*.log -Tail 20 -Wait
```

In the [admin UI](https://prism.rebus.industries/admin/) the workstation
will appear under **Workstations** with `online` status within ~5 seconds.

## Configuration file

`C:\Program Files\PRISM.Agent\agent-config.json`:

```json
{
  "prismUrl": "wss://prism.rebus.industries/ws/agent",
  "nodeName": "RB-DA2-PC01",
  "slots":    2,
  "machineId": "auto",
  "logDir":   "C:\\ProgramData\\PRISM.Agent\\logs",
  "webUiPort": 7421,
  "webUiBindAll": false
}
```

Edit + `Restart-Service PRISM.Agent` to apply changes.

## Local web UI (v0.1.28+)

The agent serves an in-process configuration page on
[`http://localhost:7421/`](http://localhost:7421/).  Right-click the tray
icon → **🌐 Open Web UI**, or open the URL directly in any browser on the
workstation.  The page exposes:

- live connection / pause / busy-slot status
- watcher **pause / resume** (disconnects WS so jobs route to other nodes)
- all of `agent-config.json` — node name, slots, roles, server URL,
  Rhino version, log dir, web UI port, LAN-binding toggle
- a tail of the in-process log buffer

Live-applied (no restart): `nodeName`, `slots`, `roles`, `logDir`.
Restart-required: `prismUrl`, `rhinoVersion`, `webUiPort`, `webUiBindAll`.

Set `webUiPort: 0` to disable the local UI entirely.  The default binds to
`localhost` only; flip `webUiBindAll: true` to expose the page on the LAN
(no auth — only do this on trusted networks).

## Roles

By default the agent advertises all three roles:

- `conversion` — accepts upload conversion jobs
- `layering`   — answers /prepare layer-inspection queries
- `receive`    — produces .3dm / .step from ORBIT versions

Disable a role in the admin UI per workstation (Workstations -> Edit)
to gate dispatch.

## Uninstall

```powershell
./uninstall.ps1
# Or, keeping logs / config:
./uninstall.ps1 -KeepData
```

## Troubleshooting

- **Service starts then immediately stops** — check `C:\ProgramData\PRISM.Agent\logs\`.
  Usually means Rhino 8 isn't installed at the expected path, or the
  Rhino.Inside bootstrap failed.
- **Service is online in admin but jobs never dispatch** — likely no
  matching format in `supportedFormats` or `isEnabled=false`. Edit the
  workstation row in the admin UI.
- **Job dispatches but fails immediately** — most often an ORBIT auth
  problem. Confirm `orbit_token` / `orbit_dev_token` are set in admin
  Settings, and that the workstation can reach `orbit-server` on the LAN.

---

## Hotpatch (dev loop, no CI rebuild)

Starting with `v0.1.22`, PRISM.Agent ships as a **multi-file** self-contained
publish (`PublishSingleFile=false` in the csproj). That means the few project
DLLs that change between iterations — `PRISM.Agent.dll`, `Orbit.Sdk.dll`,
`Orbit.Objects.dll` — can be swapped on a running workstation without going
through the 6-minute `agent.yml` build.

The full release install is **still required once per workstation** so that
the multi-file folder layout and `dotnet`/native dependencies land in
`C:\Program Files\PRISM.Agent\`. After that, iterate locally with the
hotpatch script.

### One-time SSH setup on the workstation

In an elevated PowerShell on the target workstation:

```powershell
# Enable OpenSSH Server (idempotent)
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType Automatic

# Authorise the dev-workstation key.  Replace the path with whatever
# id_ed25519_windows.pub you keep alongside id_ed25519_windows on your
# dev box (see CLAUDE.md in the REBUS System root).
$pub = Get-Content "<dev-box>\id_ed25519_windows.pub" -Raw
$auth = "$env:ProgramData\ssh\administrators_authorized_keys"
Add-Content -Path $auth -Value $pub
# Permissions on administrators_authorized_keys are strict:
icacls $auth /inheritance:r /grant "Administrators:F" /grant "SYSTEM:F"
```

(`administrators_authorized_keys` is the correct file for any user in the
Administrators group on Windows OpenSSH — *not* `~\.ssh\authorized_keys`.)

### One-line hotpatch

From the PRISM repo root on your dev box:

```powershell
pwsh tools/hotpatch-pc02.ps1 -Build
```

That will:

1. `dotnet build -c Release` on `agent/PRISM.Agent.sln`.
2. Hash `PRISM.Agent.dll`, `Orbit.Sdk.dll`, `Orbit.Objects.dll` and any
   `OrbitConnector.*.dll` in the bin output, compare against
   `tools/.cache/hotpatch-pc02.json`, SCP only the changed ones.
3. SSH in, kill any running `PRISM.Agent.exe`, restart the
   `PRISM.Agent` scheduled task.
4. Poll until the new process appears and tail the latest log.

Typical end-to-end cycle: **5–10 s** (vs ~6 min through CI). Total
transfer cost is the sum of the changed DLLs; with everything new that
is roughly 250 KB.

#### Environment overrides

| Variable           | Default                                                              |
| ------------------ | -------------------------------------------------------------------- |
| `PRISM_PC02_HOST`  | `10.0.10.202` (the script warns if you do not set it explicitly)     |
| `PRISM_PC02_USER`  | `Admin`                                                              |
| `PRISM_SSH_KEY`    | `D:\Documents\Claude\REBUS System\3DConvert\id_ed25519_windows`      |

Useful switches:

- `-Build` — recompile before hashing.  Omit to push whatever is already in `bin/Release/...`.
- `-Force` — ignore the hash cache and re-push every DLL.
- `-NoLogTail` — skip the post-restart log tail (shaves ~1 s).

### When you still need a full rebuild

The hotpatch path swaps **managed** project DLLs only. Go through the
full `agent.yml` build + release + `install.ps1` cycle when:

- a NuGet dependency changes (`Rhino.Inside`, `RhinoCommon`, `Websocket.Client`, etc.)
- the .NET 8 runtime version pinned in the publish bundle changes
- the Rhino SDK target version changes
- you edit `agent-config.example.json` or any non-DLL payload
- you bump the agent version that the PRISM server's auto-update check observes
