<#
.SYNOPSIS
  Hot-patch the project-owned DLLs of a running PRISM.Agent install on a
  remote Windows workstation (default PC02) without a full CI rebuild.

.DESCRIPTION
  PRISM.Agent now publishes as a multi-file self-contained folder (see
  agent/src/PRISM.Agent/PRISM.Agent.csproj -- PublishSingleFile=false), so
  the individual project DLLs sitting in
  `C:\Program Files\PRISM.Agent\` are swappable at runtime.

  This script:

    1. Optionally builds the agent (`-Build` switch) -- `dotnet build -c Release`
       on `agent/PRISM.Agent.sln`, which produces fresh assemblies in
       `agent/src/PRISM.Agent/bin/Release/net8.0-windows/win-x64/`.
    2. Identifies the small set of project-owned, frequently-edited DLLs
       (PRISM.Agent.dll, Orbit.Sdk.dll, Orbit.Objects.dll, plus any
       OrbitConnector.Rhino*.dll if present -- currently compile-included
       into PRISM.Agent.dll but kept here as a forward-looking case).
    3. Hashes them, compares against `.cache/hotpatch-pc02.json`, and only
       SCPs the ones that actually changed. First-time runs push everything.
    4. SCPs the changed DLLs into `C:\Program Files\PRISM.Agent\`.
    5. SSHes in and bounces the `PRISM.Agent` scheduled task so the new
       bits are loaded.  Also kills any orphaned PRISM.Agent.exe process
       the task scheduler refuses to stop cleanly.
    6. Confirms the agent came back up by tailing the latest log file and
       (optionally) checking the workstation is online on PRISM.

.PARAMETER Build
  Runs `dotnet build -c Release` before hashing/transferring.  Omit to
  hotpatch whatever is already in `bin/Release/...`.

.PARAMETER NoLogTail
  Skip the post-restart log tail (the SSH round trip adds ~1 s).

.PARAMETER Force
  Ignore the local hash cache and push every DLL regardless.

.ENVIRONMENT
  PRISM_PC02_HOST   default 10.0.10.202
  PRISM_PC02_USER   default Admin
  PRISM_SSH_KEY     default D:\Documents\Claude\REBUS System\3DConvert\id_ed25519_windows

.EXAMPLE
  pwsh tools/hotpatch-pc02.ps1 -Build
  pwsh tools/hotpatch-pc02.ps1            # skip build, push whatever is on disk
  pwsh tools/hotpatch-pc02.ps1 -Force     # ignore cache, re-push everything
#>

[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $NoLogTail,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$totalSw = [System.Diagnostics.Stopwatch]::StartNew()

# ---- Resolve repo paths ----
$repoRoot   = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'agent/src/PRISM.Agent'
$slnPath    = Join-Path $repoRoot 'agent/PRISM.Agent.sln'
$binDir     = Join-Path $projectDir 'bin/Release/net8.0-windows/win-x64'
$cacheDir   = Join-Path $repoRoot 'tools/.cache'
$cacheFile  = Join-Path $cacheDir 'hotpatch-pc02.json'

# ---- Resolve env vars + defaults ----
$pc02Host = if ($env:PRISM_PC02_HOST) { $env:PRISM_PC02_HOST } else { '10.0.10.202' }
$pc02User = if ($env:PRISM_PC02_USER) { $env:PRISM_PC02_USER } else { 'Admin' }
$sshKey   = if ($env:PRISM_SSH_KEY)   { $env:PRISM_SSH_KEY   } else {
    'D:\Documents\Claude\REBUS System\3DConvert\id_ed25519_windows'
}
$installDir = 'C:/Program Files/PRISM.Agent'
$taskName   = 'PRISM.Agent'

if (-not $env:PRISM_PC02_HOST) {
    Write-Warning "PRISM_PC02_HOST not set; defaulting to $pc02Host. Set the env var to silence this."
}
if (-not (Test-Path -LiteralPath $sshKey)) {
    throw "SSH key not found at '$sshKey' (override with `$env:PRISM_SSH_KEY)."
}

Write-Host "==> Hotpatch PRISM.Agent on ${pc02User}@${pc02Host}" -ForegroundColor Cyan
Write-Host "    ssh key   : $sshKey"
Write-Host "    install   : $installDir"
Write-Host "    task name : $taskName"

# ---- Optional build ----
if ($Build) {
    Write-Host ""
    Write-Host "==> dotnet build -c Release" -ForegroundColor Cyan
    $buildSw = [System.Diagnostics.Stopwatch]::StartNew()
    & dotnet build $slnPath -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
    $buildSw.Stop()
    Write-Host "    built in $([math]::Round($buildSw.Elapsed.TotalSeconds, 1)) s" -ForegroundColor Green
}

if (-not (Test-Path -LiteralPath $binDir)) {
    throw "Build output not found at $binDir. Re-run with -Build."
}

# ---- Discover project DLLs ----
# These are the assemblies whose source lives in this repo (or in the
# vendor submodule).  Everything else under bin/ is .NET BCL / third-party
# NuGet content that doesn't change between hotpatches.
$projectGlobs = @(
    'PRISM.Agent.dll',
    'PRISM.*.dll',
    'Orbit.Sdk.dll',
    'Orbit.Objects.dll',
    'OrbitConnector.*.dll'
)

$candidates = foreach ($glob in $projectGlobs) {
    Get-ChildItem -LiteralPath $binDir -Filter $glob -ErrorAction SilentlyContinue
}
$candidates = $candidates | Sort-Object FullName -Unique

if (-not $candidates -or $candidates.Count -eq 0) {
    throw "No project DLLs found in $binDir. Did the build fail?"
}

# ---- Load hash cache ----
$cache = @{}
if (-not $Force -and (Test-Path -LiteralPath $cacheFile)) {
    try {
        $raw = Get-Content -LiteralPath $cacheFile -Raw | ConvertFrom-Json
        foreach ($prop in $raw.PSObject.Properties) {
            $cache[$prop.Name] = $prop.Value
        }
    } catch {
        Write-Warning "Could not read $cacheFile ($_) -- treating all DLLs as new."
        $cache = @{}
    }
}

# ---- Pick which DLLs need uploading ----
$toUpload = New-Object System.Collections.Generic.List[object]
$skipped  = New-Object System.Collections.Generic.List[object]
$newCache = @{}
$totalBytes = 0
foreach ($dll in $candidates) {
    $hash = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash
    $cacheKey = "$pc02Host`::$($dll.Name)"
    $newCache[$cacheKey] = $hash
    $totalBytes += $dll.Length
    if (-not $Force -and $cache.ContainsKey($cacheKey) -and $cache[$cacheKey] -eq $hash) {
        $skipped.Add($dll) | Out-Null
    } else {
        $toUpload.Add($dll) | Out-Null
    }
}

Write-Host ""
Write-Host "==> Candidate DLLs ($($candidates.Count) total, $([math]::Round($totalBytes/1KB, 1)) KB)" -ForegroundColor Cyan
foreach ($d in $candidates) {
    $tag = if ($toUpload.Contains($d)) { '  push' } else { '  skip' }
    Write-Host ("    {0,-6} {1,-32} {2,8:N0} bytes" -f $tag, $d.Name, $d.Length)
}

if ($toUpload.Count -eq 0) {
    Write-Host ""
    Write-Host "Nothing to upload -- every DLL matches its cached hash." -ForegroundColor Yellow
    Write-Host "Use -Force to push anyway, or -Build to recompile first."
    $totalSw.Stop()
    Write-Host ("Total elapsed: {0:N1} s" -f $totalSw.Elapsed.TotalSeconds)
    return
}

# ---- Build scp argument list once (host key handling + identity) ----
$sshOpts = @(
    '-i', $sshKey,
    '-o', 'StrictHostKeyChecking=accept-new',
    '-o', 'UserKnownHostsFile=~/.ssh/known_hosts',
    '-o', 'ConnectTimeout=10',
    '-o', 'ServerAliveInterval=5'
)
$dest = "${pc02User}@${pc02Host}:'${installDir}/'"

# ---- SCP changed DLLs ----
Write-Host ""
Write-Host "==> Uploading $($toUpload.Count) DLL(s) to $dest" -ForegroundColor Cyan
$uploadSw = [System.Diagnostics.Stopwatch]::StartNew()
$uploadedBytes = 0
foreach ($dll in $toUpload) {
    $scpArgs = $sshOpts + @('-q', $dll.FullName, $dest)
    & scp @scpArgs
    if ($LASTEXITCODE -ne 0) { throw "scp failed for $($dll.Name) (exit $LASTEXITCODE)" }
    $uploadedBytes += $dll.Length
    Write-Host ("    pushed {0} ({1:N0} bytes)" -f $dll.Name, $dll.Length)
}
$uploadSw.Stop()
Write-Host ("    uploaded $([math]::Round($uploadedBytes/1KB,1)) KB in $([math]::Round($uploadSw.Elapsed.TotalSeconds,1)) s") -ForegroundColor Green

# ---- Persist cache only after successful upload ----
if (-not (Test-Path -LiteralPath $cacheDir)) {
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
}
$newCache | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $cacheFile -Encoding UTF8

# ---- Bounce the scheduled task ----
Write-Host ""
Write-Host "==> Restarting scheduled task '$taskName'" -ForegroundColor Cyan
# Single-line remote pipeline to keep the command quoting sane.  schtasks /End
# is best-effort (it returns non-zero if the task isn't currently running),
# so we explicitly swallow that failure; the killalbeit + /Run combo is what
# actually guarantees a clean restart.
$remoteCmd = @(
    "schtasks /End /TN `"$taskName`" 2>`$null;",
    "Get-Process PRISM.Agent -ErrorAction SilentlyContinue | Stop-Process -Force;",
    "Start-Sleep -Seconds 2;",
    "schtasks /Run /TN `"$taskName`" | Out-Null;",
    "Write-Output 'restart-ok'"
) -join ' '
$sshArgs = $sshOpts + @("${pc02User}@${pc02Host}", "powershell -NoProfile -Command `"$remoteCmd`"")
$restartOutput = & ssh @sshArgs
if ($LASTEXITCODE -ne 0) { throw "ssh restart failed (exit $LASTEXITCODE); output: $restartOutput" }
if ($restartOutput -notmatch 'restart-ok') {
    Write-Warning "Remote restart did not emit the expected marker. Output was: $restartOutput"
}
Write-Host "    task bounced" -ForegroundColor Green

# ---- Confirm the process came back ----
Write-Host ""
Write-Host "==> Confirming agent is back up (polling up to 10s)" -ForegroundColor Cyan
$pollSw = [System.Diagnostics.Stopwatch]::StartNew()
$alive  = $false
while ($pollSw.Elapsed.TotalSeconds -lt 10) {
    $probeCmd = "(Get-Process PRISM.Agent -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Id)"
    $probeArgs = $sshOpts + @("${pc02User}@${pc02Host}", "powershell -NoProfile -Command `"$probeCmd`"")
    $remotePid = (& ssh @probeArgs) -join "`n"
    if ($remotePid -match '^\s*(\d+)\s*$') {
        $alive = $true
        Write-Host ("    PRISM.Agent.exe pid={0} after {1:N1} s" -f $matches[1], $pollSw.Elapsed.TotalSeconds) -ForegroundColor Green
        break
    }
    Start-Sleep -Milliseconds 750
}
if (-not $alive) {
    Write-Warning "PRISM.Agent process did not come back within 10s -- check the workstation manually."
}

# ---- Tail the latest log ----
if (-not $NoLogTail) {
    Write-Host ""
    Write-Host "==> Latest agent log lines" -ForegroundColor Cyan
    $tailCmd = @(
        '$f = Get-ChildItem ''C:\ProgramData\PRISM.Agent\logs\*.log'' -ErrorAction SilentlyContinue',
        '| Sort-Object LastWriteTime -Descending | Select-Object -First 1;',
        'if ($f) { Get-Content -LiteralPath $f.FullName -Tail 8 } else { Write-Output ''(no log files found)'' }'
    ) -join ' '
    $tailArgs = $sshOpts + @("${pc02User}@${pc02Host}", "powershell -NoProfile -Command `"$tailCmd`"")
    $tail = & ssh @tailArgs
    foreach ($line in $tail) { Write-Host "    $line" }
}

$totalSw.Stop()
Write-Host ""
Write-Host ("==> Done.  Total elapsed: {0:N1} s ({1} DLL(s), {2:N1} KB uploaded)" -f `
    $totalSw.Elapsed.TotalSeconds, $toUpload.Count, ($uploadedBytes / 1KB)) -ForegroundColor Green
