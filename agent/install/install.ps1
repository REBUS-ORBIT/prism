<#
.SYNOPSIS
  Installs PRISM.Agent as a per-user tray application on the local workstation.

.DESCRIPTION
  - Copies the publish payload to C:\Program Files\PRISM.Agent\
  - Writes agent-config.json from the supplied parameters
  - Registers a Windows Task Scheduler task "PRISM.Agent" that launches
    PRISM.Agent.exe at user logon (runs as the current interactive user,
    highest available privileges, hidden window).
  - Does NOT register a Windows service — the agent runs as an interactive
    tray process, which is required for Rhino.Inside to create windows and
    display licence dialogs.

.PARAMETER PrismUrl
  The PRISM server WS URL (e.g. wss://prism.rebus.industries/ws/agent).

.PARAMETER NodeName
  Human-readable name shown in the admin UI. Defaults to the machine name.

.PARAMETER Slots
  Concurrent worker slots this agent exposes. Defaults to 1.

.PARAMETER LaunchNow
  If specified, starts the agent immediately after installation.

.EXAMPLE
  pwsh ./install.ps1 -PrismUrl wss://prism.rebus.industries/ws/agent -NodeName RB-DA2-PC01 -Slots 2
#>

param(
    [Parameter(Mandatory)] [string] $PrismUrl,
    [string] $NodeName  = $env:COMPUTERNAME,
    [int]    $Slots     = 1,
    [string] $InstallDir = "C:\Program Files\PRISM.Agent",
    [string] $DataDir    = "C:\ProgramData\PRISM.Agent",
    [switch] $LaunchNow
)

$ErrorActionPreference = 'Stop'

# Elevation is required to write to Program Files and create the scheduled task.
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "install.ps1 must be run from an elevated PowerShell."
}

Write-Host "PRISM.Agent installer (tray mode)"
Write-Host "  Server    : $PrismUrl"
Write-Host "  Node      : $NodeName"
Write-Host "  Slots     : $Slots"
Write-Host "  InstallDir: $InstallDir"
Write-Host "  DataDir   : $DataDir"

# ---- Create directories ----
New-Item -Path $InstallDir -ItemType Directory -Force | Out-Null
New-Item -Path $DataDir    -ItemType Directory -Force | Out-Null
New-Item -Path (Join-Path $DataDir 'logs') -ItemType Directory -Force | Out-Null

# ---- Copy payload ----
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Get-ChildItem -Path $scriptRoot -Filter PRISM.Agent.exe -Recurse | Select-Object -First 1
if (-not $payload) { throw "PRISM.Agent.exe not found alongside install.ps1" }

Write-Host "Copying payload from $($payload.DirectoryName) -> $InstallDir"
Copy-Item -Path (Join-Path $payload.DirectoryName '*') -Destination $InstallDir -Recurse -Force

# ---- Write agent-config.json ----
$config = [ordered]@{
    prismUrl     = $PrismUrl
    nodeName     = $NodeName
    slots        = $Slots
    logDir       = (Join-Path $DataDir 'logs')
    machineId    = 'auto'
    rhinoVersion = 'auto'
} | ConvertTo-Json -Depth 4
$configPath = Join-Path $InstallDir 'agent-config.json'
Set-Content -Path $configPath -Value $config -Encoding UTF8
Write-Host "Wrote $configPath"

# ---- Register Task Scheduler task ----
$taskName = 'PRISM.Agent'
$exePath  = Join-Path $InstallDir 'PRISM.Agent.exe'

# Remove any pre-existing task with the same name.
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing scheduled task '$taskName'..."
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

# The task runs under the current (interactive) user account.
$currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$action  = New-ScheduledTaskAction  -Execute $exePath
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Days 3650) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew

$principal = New-ScheduledTaskPrincipal `
    -UserId $currentUser `
    -LogonType Interactive `
    -RunLevel Highest

Write-Host "Registering scheduled task '$taskName' for user $currentUser..."
Register-ScheduledTask `
    -TaskName    $taskName `
    -Action      $action `
    -Trigger     $trigger `
    -Settings    $settings `
    -Principal   $principal `
    -Description 'REBUS-ORBIT PRISM conversion agent (Rhino.Inside tray process)' | Out-Null

Write-Host "Scheduled task registered. The agent will start automatically at next logon."

# ---- Optionally start immediately ----
if ($LaunchNow) {
    Write-Host "Starting agent now..."
    Start-ScheduledTask -TaskName $taskName
}

Write-Host ""
Write-Host "Installation complete."
Write-Host "  Config  : $configPath"
Write-Host "  Logs    : $(Join-Path $DataDir 'logs')"
Write-Host "  Task    : $taskName (runs at logon for $currentUser)"
