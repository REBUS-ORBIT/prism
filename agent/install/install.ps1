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
    [int]    $WebUiPort  = 7421,
    [switch] $WebUiLocalhostOnly,
    [switch] $LaunchNow,
    [switch] $ForceConfig
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

# ---- Grant Users:Modify on the install dir ----
# The in-app auto-updater extracts a new agent zip on top of $InstallDir.
# By default Program Files is read-only for non-admin users, so the updater
# silently fails on workstations whose interactive login user isn't a local
# admin (RunLevel=Highest cannot promote a non-admin to admin).  Granting
# Modify to BUILTIN\Users on $InstallDir lets the agent's PowerShell child
# process overwrite its own DLLs without elevation.  Acceptable trade-off
# given the agent already trusts code shipped under this directory.
Write-Host "Granting BUILTIN\Users Modify on $InstallDir..."
& icacls $InstallDir /grant "*S-1-5-32-545:(OI)(CI)M" /T 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "icacls returned $LASTEXITCODE; auto-update may fail for non-admin users."
}

# ---- Copy payload ----
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Get-ChildItem -Path $scriptRoot -Filter PRISM.Agent.exe -Recurse | Select-Object -First 1
if (-not $payload) { throw "PRISM.Agent.exe not found alongside install.ps1" }

# When install.ps1 is invoked from inside the install directory (e.g. by the
# Inno Setup wizard which copies the payload there itself), skip the redundant
# copy step.  Otherwise this is the classic "expand the zip then run install.ps1"
# flow where we copy the payload into Program Files ourselves.
$payloadDir = $payload.DirectoryName
$resolvedInstall = (Resolve-Path $InstallDir).Path
if ($payloadDir -and ((Resolve-Path $payloadDir).Path -ieq $resolvedInstall)) {
    Write-Host "Payload already in $InstallDir (skipping copy)"
} else {
    Write-Host "Copying payload from $payloadDir -> $InstallDir"
    Copy-Item -Path (Join-Path $payloadDir '*') -Destination $InstallDir -Recurse -Force
}

# ---- Write agent-config.json (ProgramData, user-writable) ----
# v0.1.31+: agent-config.json lives in ProgramData so the agent (which runs
# as the interactive workstation user, not necessarily an admin) can persist
# changes from the web UI without ACL errors.  Existing configs in the legacy
# Program Files path are preserved on first read; the agent's Save() drops
# them once a ProgramData copy exists.
$configPath = Join-Path $DataDir 'agent-config.json'
$legacyConfigPath = Join-Path $InstallDir 'agent-config.json'

if ((Test-Path $configPath) -and -not $ForceConfig) {
    Write-Host "Preserving existing $configPath (-ForceConfig to overwrite)"
} elseif ((Test-Path $legacyConfigPath) -and -not $ForceConfig) {
    Write-Host "Migrating legacy $legacyConfigPath -> $configPath"
    Copy-Item -Path $legacyConfigPath -Destination $configPath -Force
} else {
    $bindAll = -not $WebUiLocalhostOnly
    $config = [ordered]@{
        prismUrl     = $PrismUrl
        nodeName     = $NodeName
        slots        = $Slots
        roles        = @('conversion', 'layering', 'receive')
        logDir       = (Join-Path $DataDir 'logs')
        machineId    = 'auto'
        rhinoVersion = 'auto'
        webUiPort    = $WebUiPort
        webUiBindAll = $bindAll
    } | ConvertTo-Json -Depth 4
    Set-Content -Path $configPath -Value $config -Encoding UTF8
    Write-Host "Wrote $configPath"
}

# Make sure the agent (running as the interactive user) can read+write the
# ProgramData copy.  ProgramData is normally Authenticated-Users:Modify by
# default, but be defensive against locked-down golden images.
icacls $DataDir /grant "*S-1-5-11:(OI)(CI)M" /T 2>$null | Out-Null

# ---- Web UI URL ACL + firewall (LAN access without admin) ----
# By default the agent binds to http://+:7421/ so the page is reachable from
# anywhere on the LAN.  HttpListener on a non-localhost prefix needs either
# admin or a URL ACL grant; we register one here so the (non-admin) agent
# user can bind. Pass -WebUiLocalhostOnly to skip both the ACL grant and
# the firewall rule.
if (-not $WebUiLocalhostOnly) {
    $aclUrl = "http://+:$WebUiPort/"
    Write-Host "Registering URL ACL $aclUrl for Authenticated Users..."
    # Drop any prior grant for the same prefix; ignore errors when there is
    # nothing to remove.
    & netsh http delete urlacl url=$aclUrl 2>$null | Out-Null
    & netsh http add urlacl url=$aclUrl user="NT AUTHORITY\Authenticated Users" | Out-Null

    $fwName = "PRISM Agent Web UI ($WebUiPort)"
    Write-Host "Adding firewall rule '$fwName'..."
    Remove-NetFirewallRule -DisplayName $fwName -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -DisplayName $fwName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $WebUiPort -Profile Any | Out-Null
} else {
    Write-Host "WebUiLocalhostOnly: skipping URL ACL grant + firewall rule"
}

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

$action = New-ScheduledTaskAction -Execute $exePath

# v0.1.34: add a second trigger at system startup so the agent's WS
# connection (and the local web UI) comes up as early as possible after
# a reboot.  Combined with the existing -AtLogOn trigger this also
# serves as a belt-and-braces fallback if a botched in-app updater ever
# exits the process without successfully relaunching it: instead of
# being agentless until the next interactive logon, the scheduled task
# will try to bring the agent back up at boot.
#
# RestartCount=3 + RestartInterval=1m below applies to BOTH triggers,
# so a transient launch failure (locked DLL during extract, etc.) gets
# three retries inside the first three minutes regardless of how the
# task was kicked off.
#
# NOTE on LogonType=Interactive (preserved from earlier releases): with
# Interactive logon, the -AtStartup trigger only fires once the
# configured user has an interactive session. If no one is logged on
# at boot, the trigger queues until the next logon (same end-state as
# the existing AtLogOn-only setup). The session 0 guard in Program.cs
# is defensive insurance in case the principal is ever moved to S4U /
# Password (which would let the task fire pre-logon in session 0).
$triggerLogon   = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$triggerStartup = New-ScheduledTaskTrigger -AtStartup
$triggers       = @($triggerLogon, $triggerStartup)

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
Write-Host "  Triggers: AtLogOn (user=$currentUser), AtStartup"
Write-Host "  Restart : up to 3 times, 1 min apart, on failure"
Register-ScheduledTask `
    -TaskName    $taskName `
    -Action      $action `
    -Trigger     $triggers `
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
