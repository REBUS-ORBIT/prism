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

1. **Download the latest agent zip** from the [releases page](https://github.com/REBUS-ORBIT/prism/releases/latest)
   (file: `PRISM.Agent-vX.Y.Z.zip`).
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
     -Slots 2
   ```

   - `PrismUrl`: the agent WS endpoint (use `ws://10.0.200.211:8765/ws/agent`
     for LAN-direct, bypassing Caddy)
   - `NodeName`: friendly name surfaced in the admin pool
   - `Slots`: how many concurrent conversion jobs this machine handles
     (recommended: number of physical cores ÷ 2, capped at 4)

   The installer:
   - copies the payload to `C:\Program Files\PRISM.Agent\`
   - writes `agent-config.json`
   - registers + starts the `PRISM.Agent` Windows service
   - configures the service to restart automatically on failure

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
  "logDir":   "C:\\ProgramData\\PRISM.Agent\\logs"
}
```

Edit + `Restart-Service PRISM.Agent` to apply changes.

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
