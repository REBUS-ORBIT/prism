using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace PRISM.Agent.Tray;

/// <summary>
/// Checks for new agent releases on GitHub and performs an in-place update
/// by launching a foreground PowerShell script that replaces the binary
/// after the current process exits.
/// </summary>
public static class Updater
{
    const string ReleasesUrl =
        "https://api.github.com/repos/REBUS-ORBIT/prism-agent/releases/latest";

    static readonly Version _currentVersion =
        typeof(Updater).Assembly.GetName().Version ?? new Version(0, 1, 0);

    // ------------------------------------------------------------------

    /// <summary>
    /// Result of <see cref="CheckForUpdateAsync"/>.  <see cref="SizeBytes"/>
    /// and <see cref="Notes"/> come straight from the GitHub release JSON
    /// when present (size on the zip asset; body on the release itself)
    /// so the tray "Update available" dialog can show download size and a
    /// preview of release notes without making a second API call.
    /// </summary>
    public sealed record UpdateInfo(
        string TagName,
        string DownloadUrl,
        Version NewVersion,
        long? SizeBytes = null,
        string? Notes = null);

    // ------------------------------------------------------------------

    /// <summary>
    /// Returns <c>null</c> if already up to date, otherwise the available update.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"PRISM.Agent/{_currentVersion} (Windows)");

        string json;
        try
        {
            json = await http.GetStringAsync(ReleasesUrl);
        }
        catch
        {
            // No network / private repo — treat as up-to-date.
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root    = doc.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
        if (string.IsNullOrEmpty(tagName)) return null;

        string? notes = root.TryGetProperty("body", out var nb) ? nb.GetString() : null;

        string? downloadUrl = null;
        long?   sizeBytes   = null;
        if (root.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array &&
            assets.GetArrayLength() > 0)
        {
            // Prefer the multi-file publish .zip — that is what the
            // PowerShell update script knows how to extract over the install
            // dir.  Fall back to the first asset only if no .zip is present
            // (defensive; the agent.yml workflow always uploads a zip first).
            // The wizard installer (.exe) is intentionally NOT auto-applied
            // because it is interactive (UAC prompt + finish-page checkboxes).
            for (int i = 0; i < assets.GetArrayLength(); i++)
            {
                var a = assets[i];
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name != null &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    a.TryGetProperty("browser_download_url", out var bu))
                {
                    downloadUrl = bu.GetString();
                    if (a.TryGetProperty("size", out var sz) &&
                        sz.ValueKind == JsonValueKind.Number)
                    {
                        sizeBytes = sz.GetInt64();
                    }
                    break;
                }
            }
            if (downloadUrl is null)
            {
                downloadUrl = assets[0].TryGetProperty("browser_download_url", out var bu0)
                    ? bu0.GetString()
                    : null;
                if (assets[0].TryGetProperty("size", out var sz0) &&
                    sz0.ValueKind == JsonValueKind.Number)
                {
                    sizeBytes = sz0.GetInt64();
                }
            }
        }

        if (!Version.TryParse(tagName.TrimStart('v'), out var newVersion))
            return null;

        return newVersion > _currentVersion
            ? new UpdateInfo(tagName, downloadUrl ?? "", newVersion, sizeBytes, notes)
            : null;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Path the update PowerShell script writes its diagnostic log to.
    /// Survives across the agent restart so the next launch can surface
    /// the result of the last update attempt.
    /// </summary>
    static string UpdateLogPath =>
        Path.Combine(Path.GetTempPath(), "PRISM.Agent.Update.log");

    /// <summary>
    /// Path the in-process <see cref="DownloadAndInstallAsync"/> writes
    /// the target version to BEFORE calling <see cref="Application.Exit"/>.
    /// The relaunched agent reads it to show a "Updated to vX.Y.Z" tray
    /// balloon, then deletes it so the balloon only fires once.
    /// </summary>
    static string NewVersionMarkerPath =>
        Path.Combine(Path.GetTempPath(), "PRISM.Agent.Update.NewVersion");

    /// <summary>
    /// True when the agent's install directory is writable by the current
    /// user.  When false, the in-app updater cannot extract the new zip
    /// in place and will silently fail; we surface that to the operator
    /// instead of pretending the update succeeded.
    /// </summary>
    public static bool IsInstallDirWritable()
    {
        var probe = Path.Combine(
            AppContext.BaseDirectory,
            ".update-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the trailing portion of the most recent update log if it
    /// contains a fatal marker and post-dates this process's start time.
    /// Used by the tray to surface a "last update failed" message when
    /// the agent is relaunched after a botched update.
    /// </summary>
    public static string? GetLastUpdateFailure()
    {
        try
        {
            if (!File.Exists(UpdateLogPath)) return null;
            var fi = new FileInfo(UpdateLogPath);
            // Only care about logs younger than 10 minutes -- anything
            // older was a previous session the operator already saw.
            if (fi.LastWriteTime < DateTime.Now.AddMinutes(-10)) return null;

            var text = File.ReadAllText(UpdateLogPath);
            if (text.Contains("FATAL", StringComparison.Ordinal) ||
                text.Contains("ERROR", StringComparison.Ordinal))
            {
                return text;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One-shot read of the "we just updated to vX.Y.Z" marker file
    /// stashed by <see cref="DownloadAndInstallAsync"/> before the
    /// previous agent exited.  Returns the recorded tag when the marker
    /// file is present, younger than 10 minutes, and the recorded
    /// version matches the now-running assembly version.  The marker is
    /// deleted on return so the post-update tray balloon only fires
    /// once per actual upgrade.
    /// </summary>
    public static string? ConsumeLastUpdateSuccess()
    {
        try
        {
            if (!File.Exists(NewVersionMarkerPath)) return null;
            var fi = new FileInfo(NewVersionMarkerPath);
            if (fi.LastWriteTime < DateTime.Now.AddMinutes(-10))
            {
                // Stale marker from a prior, abandoned run.  Wipe it.
                try { File.Delete(NewVersionMarkerPath); } catch { /* nop */ }
                return null;
            }

            var recorded = File.ReadAllText(NewVersionMarkerPath).Trim();

            // The marker may be a tag ("v0.1.34") or a bare version ("0.1.34").
            // Compare on the trimmed numeric form.
            var recordedVersion = recorded.TrimStart('v', 'V');
            var currentVersion  = _currentVersion.ToString();

            // Match exactly OR by leading prefix (assembly version is
            // "0.1.34.0" but the tag is "v0.1.34").
            bool versionsMatch =
                currentVersion.StartsWith(recordedVersion, StringComparison.Ordinal) ||
                recordedVersion.StartsWith(currentVersion, StringComparison.Ordinal);

            // One-shot: delete regardless of match so a stale marker can't
            // re-fire on every relaunch.
            try { File.Delete(NewVersionMarkerPath); } catch { /* nop */ }

            return versionsMatch ? recorded : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the update zip, then launches a foreground PowerShell
    /// script that waits for this process to exit, extracts the zip over
    /// the install directory, and relaunches the agent.
    /// Calls <see cref="Application.Exit"/> after scheduling the script.
    /// </summary>
    /// <remarks>
    /// v0.1.34: the PowerShell helper window is now intentionally VISIBLE
    /// (<c>CreateNoWindow=false</c>, <c>WindowStyle=Normal</c>) and mirrors
    /// the same step lines to <c>Write-Host</c> that go to the diagnostic
    /// log file.  The user-visible terminal window stays open if a FATAL
    /// error occurs (Read-Host pause) so the operator can copy the message
    /// before retrying.  On the happy path the window closes itself when
    /// the new agent launches.  This replaces the v0.1.32 silent flow
    /// (<c>CreateNoWindow=true</c>) which left the user staring at nothing
    /// after the old agent exited.
    /// </remarks>
    public static async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<int> progress)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
            throw new InvalidOperationException("No download URL in the release.");

        // Pre-flight: verify we can actually overwrite the install dir.
        // On workstations whose interactive user is not a local admin and
        // whose install was done via the legacy install.ps1 (pre-v0.1.32,
        // no Users:Modify grant), Program Files is read-only and the
        // updater would silently fail.  Fail loudly instead.
        if (!IsInstallDirWritable())
        {
            throw new UnauthorizedAccessException(
                "The agent's install directory is not writable by this Windows " +
                "user, so the in-app updater cannot replace the running binaries. " +
                "Please re-run PRISM.Agent-Setup.exe (run as administrator) once " +
                "to grant write access -- future in-app updates will then work " +
                "without elevation.");
        }

        var tempZip = Path.Combine(Path.GetTempPath(), "PRISM.Agent.Update.zip");

        // --- Download ---
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"PRISM.Agent/{_currentVersion} (Windows)");

        using var resp = await http.GetAsync(
            info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? info.SizeBytes ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(tempZip);
        var buf        = new byte[65536];
        long downloaded = 0;
        int  read;
        while ((read = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read));
            downloaded += read;
            if (totalBytes > 0)
                progress.Report((int)(downloaded * 100 / totalBytes));
        }
        progress.Report(100);

        // --- Schedule the replacement via PowerShell ---
        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var pid        = Environment.ProcessId;
        var exePath    = Path.Combine(installDir, "PRISM.Agent.exe");
        var logPath    = UpdateLogPath;
        var tag        = info.TagName;

        // Wipe any stale log from a previous attempt so the diagnostic-on-
        // next-startup hook only sees this run.
        try { if (File.Exists(logPath)) File.Delete(logPath); } catch { /* nop */ }

        // Stash the target version so the relaunched agent can show a
        // "Updated to vX.Y.Z" tray balloon without text-matching the log.
        try { File.WriteAllText(NewVersionMarkerPath, tag); } catch { /* nop */ }

        // Single-quoted strings inside the PS script escape ' as ''.
        // Both Write-Host (visible terminal) and Add-Content (durable log)
        // get every step line so users see progress AND we keep the
        // post-mortem diagnostic file the next agent boot inspects.
        var ps = $@"
$ErrorActionPreference = 'Stop'
$log = '{Esc(logPath)}'
$Host.UI.RawUI.WindowTitle = 'PRISM Agent — Updating to {Esc(tag)}'
function W($m) {{
    $line = ""[$([DateTime]::Now.ToString('HH:mm:ss'))] "" + $m
    Add-Content -Path $log -Value $line
    Write-Host $line
}}

W 'PRISM Agent updater — keep this window open until it closes itself'
W 'target version: {Esc(tag)}'

$fatal = $false
try {{
    W 'update script started'
    $proc = Get-Process -Id {pid} -ErrorAction SilentlyContinue
    if ($proc) {{
        W 'waiting for agent pid {pid} to exit'
        $null = $proc.WaitForExit(60000)
        W 'agent exited'
    }} else {{
        W 'agent already exited'
    }}
    Start-Sleep -Milliseconds 500
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    W 'extracting {Esc(tempZip)} -> {Esc(installDir)}'
    [IO.Compression.ZipFile]::ExtractToDirectory('{Esc(tempZip)}', '{Esc(installDir)}', $true)
    W 'extraction complete'
    if (Test-Path '{Esc(exePath)}') {{
        W 'launching new agent'
        Start-Process -FilePath '{Esc(exePath)}'
        W 'launched'
    }} else {{
        $fatal = $true
        W ""ERROR: exe not found at '{Esc(exePath)}'""
    }}
}} catch {{
    $fatal = $true
    W ""FATAL: $_""
    if ($_.ScriptStackTrace) {{ W $_.ScriptStackTrace }}
}}

if ($fatal) {{
    Write-Host ''
    Write-Host '------------------------------------------------------------'
    Write-Host 'Update FAILED. The diagnostic log is at:'
    Write-Host ""  $log""
    Write-Host 'Please copy the lines above before closing this window.'
    Write-Host '------------------------------------------------------------'
    try {{ [void](Read-Host 'Press Enter to close') }} catch {{ Start-Sleep -Seconds 30 }}
}} else {{
    # Brief grace so the user sees the 'launched' line before the
    # window closes itself.
    Start-Sleep -Seconds 2
}}
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            // UseShellExecute=false lets us control window creation
            // explicitly: with CreateNoWindow=false the child gets a
            // fresh console host attached.  The agent itself is a
            // WinForms tray app with no console, so without an
            // explicit console here the Write-Host lines have nowhere
            // to render.
            UseShellExecute = false,
            // v0.1.34: visible window so the user can see download +
            // extract + relaunch progress instead of "agent closes,
            // nothing happens."  Pre-v0.1.34 used CreateNoWindow=true,
            // which made silent failures indistinguishable from
            // success and was the proximate cause of the v0.1.33 bug
            // report from RB-DA2-PC02.
            CreateNoWindow  = false,
            WindowStyle     = ProcessWindowStyle.Normal,
        });

        Application.Exit();
    }

    static string Esc(string path) => path.Replace("'", "''");
}
