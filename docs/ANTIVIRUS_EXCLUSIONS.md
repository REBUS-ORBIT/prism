# Antivirus exclusions for PRISM Visualiser workstations

UE 5.7 + Pixel Streaming + the orchestrator's per-run filesystem caching
hit a few patterns Windows Defender and other AV tools love to scan
aggressively. Without exclusions, cold-start times triple and the import
pipeline competes with realtime scans for I/O bandwidth.

This applies to **workstations that run the Visualiser role**. Conversion-only
workstations don't need any of this.

---

## TL;DR

Run [`install/Set-VisualiserAvExclusions.ps1`](#powershell-helper) from an
elevated PowerShell on the workstation. Or apply the exclusions manually
per the [Folders](#folders-to-exclude) / [Processes](#processes-to-exclude-from-real-time-scanning)
lists below.

---

## Folders to exclude

| Path                                              | Why                                                                                                                                                |
| ------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `%LOCALAPPDATA%\PRISM.Visualiser\`                | Orchestrator cache (ORBIT version blobs, content-addressed store) + per-run staging.                                                               |
| `%LOCALAPPDATA%\PRISM.Visualiser\runs\`           | Per-run UE project copies. Each run extracts the ~50 MB template zip and writes a fresh `.uproject`; Defender scans every file on extract.        |
| `%LOCALAPPDATA%\PRISM.Agent\`                     | Agent state (config, logs, cached UE template downloads).                                                                                          |
| `C:\ProgramData\PRISM.Agent\`                     | Per-machine agent config (`agent-config.json`), logs.                                                                                              |
| `<UE_ROOT>\Engine\Saved\`                         | UE shader cache, derived data. Hit on every Unreal start — Defender scanning these triples shader compile time on UE first-run.                    |
| `<UE_ROOT>\Engine\Intermediate\`                  | Build intermediate (irrelevant for runtime but Defender scans here heavily during the first launch after a UE update).                             |
| `C:\Program Files\PRISM.Agent\`                   | Agent install dir (sidecar EXEs, including `prism-visualiser.exe`).                                                                                |

Default `UE_ROOT` is `C:\Program Files\Epic Games\UE_5.7\`. If your install
is elsewhere, adjust accordingly.

### Observed impact (RB-DA2-PC01, Defender real-time scan enabled)

| Step                                | Without exclusions | With exclusions |
| ----------------------------------- | ------------------ | --------------- |
| UE first-run shader compile         | **250 s**          | 90 s            |
| glTF import via Interchange         | **75 s**           | 28 s            |
| Per-run template clone (extract zip)| **45 s**           | 12 s            |

Net: a cold start drops from **~6 min to ~2-3 min**. Warm starts go from
~4 s to ~2 s (the warm path doesn't touch the template zip but still
opens hundreds of DDC files).

---

## Processes to exclude from real-time scanning

| Process                  | Why                                                                                       |
| ------------------------ | ----------------------------------------------------------------------------------------- |
| `UnrealEditor.exe`       | The UE editor running in `-game` mode for Pixel Streaming.                                |
| `UnrealEditor-Cmd.exe`   | The headless Python-script runner used by `import_orbit.py` and `import_mvr.py`.          |
| `prism-visualiser.exe`   | The orchestrator (spawned per run by the agent).                                          |
| `PRISM.Agent.exe`        | The long-lived agent tray process.                                                        |
| `node.exe`               | Cirrus signalling server. Runs as a child of `prism-visualiser.exe`.                      |

> **Note on `node.exe`:** if your workstation runs other Node apps (most do
> not — these are dedicated render boxes), the exclusion will broadly skip
> them too. If that's a concern, use the per-path exclusion on
> `<orchestrator-install-dir>\signalling\node.exe` only. The orchestrator
> ships a vendored `node.exe` for exactly this scoping.

---

## How to apply

### PowerShell helper

PRISM ships a one-shot script that idempotently applies the recommended
set. Run from an elevated PowerShell:

```powershell
# From the workstation, with elevation:
& "C:\Program Files\PRISM.Agent\install\Set-VisualiserAvExclusions.ps1"
```

The script is documented inline; it logs every `Add-MpPreference` it
issues and exits 0 even if some exclusions were already present.

### Manual — PowerShell

```powershell
# Folders
Add-MpPreference -ExclusionPath "$env:LOCALAPPDATA\PRISM.Visualiser"
Add-MpPreference -ExclusionPath "$env:LOCALAPPDATA\PRISM.Agent"
Add-MpPreference -ExclusionPath "$env:ProgramData\PRISM.Agent"
Add-MpPreference -ExclusionPath "C:\Program Files\PRISM.Agent"
Add-MpPreference -ExclusionPath "C:\Program Files\Epic Games\UE_5.7\Engine\Saved"
Add-MpPreference -ExclusionPath "C:\Program Files\Epic Games\UE_5.7\Engine\Intermediate"

# Processes
Add-MpPreference -ExclusionProcess "UnrealEditor.exe"
Add-MpPreference -ExclusionProcess "UnrealEditor-Cmd.exe"
Add-MpPreference -ExclusionProcess "prism-visualiser.exe"
Add-MpPreference -ExclusionProcess "PRISM.Agent.exe"
# Optional — see note above
Add-MpPreference -ExclusionProcess "node.exe"
```

### Group Policy (recommended for fleet rollout)

Computer Configuration → Administrative Templates → Windows Components →
Microsoft Defender Antivirus → Exclusions:

-   **Path Exclusions** — add each folder from the table above.
-   **Process Exclusions** — add each process from the table above.

The GPO applies on next group-policy refresh (~90 min) or after running
`gpupdate /force` on the workstation.

### Verify

```powershell
Get-MpPreference | Select-Object -ExpandProperty ExclusionPath
Get-MpPreference | Select-Object -ExpandProperty ExclusionProcess
```

The two lists should include everything from the tables above.

---

## Other AV products

The same patterns apply. The folders/processes are universal; only the
syntax for adding exclusions changes.

| Product               | Exclusion docs                                                                                                                                                                |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **ESET**              | Setup → Computer protection → Real-time file system protection → Edit → Exclusions.                                                                                           |
| **Sophos Central**    | Endpoint policies → Threat Protection → Real-time scanning → Exclusions. Add folders + processes per the tables above.                                                        |
| **Trend Micro Apex One** | Web console → Agents → Agent Management → Settings → Scan Exclusion List.                                                                                                  |
| **CrowdStrike Falcon**| Configuration → Prevention Policies → ML Exclusions / IOA Exclusions. Path-based exclusions go under ML; CrowdStrike does not have a notion of "process exclusion" — file hashes are used instead. |
| **Symantec Endpoint** | Policies → Exceptions → Add → File / Folder / Application.                                                                                                                    |

If you run an AV product not listed here, refer to its vendor's
documentation for "folder exclusion" and "process exclusion". The
fundamentals are universal: Defender's behaviour we measured here will
reproduce on any realtime-scanning AV unless these paths are excluded.

---

## Audit

These exclusions reduce the AV surface area on the workstation. They are
**safe** because:

1.  The PRISM agent already trusts everything under
    `C:\Program Files\PRISM.Agent\` and the per-user cache directories —
    code there is shipped by the auto-updater from `github.com/REBUS-ORBIT/prism`.
2.  The UE engine binaries are signed by Epic Games (verifiable via
    `signtool verify /pa "UnrealEditor.exe"`).
3.  The per-run staging dirs are wiped at the end of every run (or on
    next agent restart at the latest), so any unexpected file deposited
    there has a short lifetime.

If your security policy forbids AV exclusions outright, the visualiser
will still **work** — it'll just be 3-4x slower on cold starts. Surface
the timing impact to the user via your own progress UI.
