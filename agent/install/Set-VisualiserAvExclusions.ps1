<#
.SYNOPSIS
  Add the Windows Defender exclusions recommended for PRISM Visualiser
  workstations. Idempotent: re-running is a no-op for entries already
  present.

.DESCRIPTION
  UE 5.7 + Pixel Streaming + the orchestrator's per-run filesystem
  caching trigger heavy real-time scanning on Defender's default
  configuration. Without exclusions, cold-start times triple. See
  docs/ANTIVIRUS_EXCLUSIONS.md for the per-folder rationale.

  This script is OPT-IN. It is bundled with the agent installer but
  not run automatically — fleet operators should opt in once they've
  confirmed the exclusion set matches their security policy.

.PARAMETER UnrealEngineRoot
  Path to the UE 5.7 install. Defaults to `C:\Program Files\Epic Games\UE_5.7\`.

.PARAMETER IncludeNodeExe
  Also exclude `node.exe` from real-time scanning. The Cirrus signalling
  server runs as a node child process. Excluding `node.exe` broadly
  covers it but may also exclude other Node apps on the workstation
  (rare on dedicated render boxes). Default: $false.

.EXAMPLE
  & 'C:\Program Files\PRISM.Agent\install\Set-VisualiserAvExclusions.ps1'

.EXAMPLE
  & 'C:\Program Files\PRISM.Agent\install\Set-VisualiserAvExclusions.ps1' `
    -UnrealEngineRoot 'D:\UnrealEngine\UE_5.7' -IncludeNodeExe
#>

param(
    [string] $UnrealEngineRoot = 'C:\Program Files\Epic Games\UE_5.7',
    [switch] $IncludeNodeExe
)

$ErrorActionPreference = 'Stop'

# Elevation required (Defender preferences are HKLM-backed).
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Set-VisualiserAvExclusions.ps1 must be run from an elevated PowerShell."
}

# Defender service must be present (i.e. not a tertiary AV-only box).
$mp = Get-MpComputerStatus -ErrorAction SilentlyContinue
if (-not $mp) {
    Write-Warning "Windows Defender is not the active AV on this machine. See docs/ANTIVIRUS_EXCLUSIONS.md for ESET/Sophos/Trend Micro/CrowdStrike equivalents."
    return
}

$paths = @(
    "$env:LOCALAPPDATA\PRISM.Visualiser"
    "$env:LOCALAPPDATA\PRISM.Agent"
    "$env:ProgramData\PRISM.Agent"
    "C:\Program Files\PRISM.Agent"
    (Join-Path $UnrealEngineRoot 'Engine\Saved')
    (Join-Path $UnrealEngineRoot 'Engine\Intermediate')
)

$processes = @(
    'UnrealEditor.exe'
    'UnrealEditor-Cmd.exe'
    'prism-visualiser.exe'
    'PRISM.Agent.exe'
)

if ($IncludeNodeExe) {
    $processes += 'node.exe'
}

$existingPaths     = @(Get-MpPreference | Select-Object -ExpandProperty ExclusionPath)
$existingProcesses = @(Get-MpPreference | Select-Object -ExpandProperty ExclusionProcess)

$added = 0
foreach ($p in $paths) {
    if ($existingPaths -contains $p) {
        Write-Host "[skip] path already excluded: $p"
        continue
    }
    Write-Host "[add ] path:    $p"
    Add-MpPreference -ExclusionPath $p
    $added++
}
foreach ($proc in $processes) {
    if ($existingProcesses -contains $proc) {
        Write-Host "[skip] process already excluded: $proc"
        continue
    }
    Write-Host "[add ] process: $proc"
    Add-MpPreference -ExclusionProcess $proc
    $added++
}

Write-Host ""
Write-Host "Added $added new exclusions."
Write-Host "Verify with:"
Write-Host "  Get-MpPreference | Select-Object -ExpandProperty ExclusionPath"
Write-Host "  Get-MpPreference | Select-Object -ExpandProperty ExclusionProcess"
