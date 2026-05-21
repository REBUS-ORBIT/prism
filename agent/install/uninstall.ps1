<#
.SYNOPSIS  Removes the PRISM.Agent scheduled task, running process, and on-disk payload.
#>
param(
    [string] $InstallDir = "C:\Program Files\PRISM.Agent",
    [string] $DataDir    = "C:\ProgramData\PRISM.Agent",
    [switch] $KeepData
)

$ErrorActionPreference = 'Stop'
$taskName = 'PRISM.Agent'

# ---- Unregister scheduled task ----
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping and removing scheduled task '$taskName'..."
    # Stop the task if it's currently running.
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
} else {
    Write-Host "Scheduled task '$taskName' not found — skipping."
}

# ---- Kill any running agent process ----
$proc = Get-Process -Name 'PRISM.Agent' -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Terminating running PRISM.Agent process (PID $($proc.Id))..."
    $proc | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

# ---- Remove install directory ----
if (Test-Path $InstallDir) {
    Write-Host "Removing $InstallDir"
    Remove-Item -Path $InstallDir -Recurse -Force
} else {
    Write-Host "Install directory $InstallDir not found — skipping."
}

# ---- Optionally remove data directory ----
if (-not $KeepData -and (Test-Path $DataDir)) {
    Write-Host "Removing $DataDir"
    Remove-Item -Path $DataDir -Recurse -Force
} elseif ($KeepData) {
    Write-Host "Keeping data directory at $DataDir (--KeepData was specified)"
}

Write-Host ""
Write-Host "Uninstall complete."
