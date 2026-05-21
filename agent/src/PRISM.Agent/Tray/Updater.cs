using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace PRISM.Agent.Tray;

/// <summary>
/// Checks for new agent releases on GitHub and performs an in-place update
/// by launching a background PowerShell script that replaces the binary
/// after the current process exits.
/// </summary>
public static class Updater
{
    const string ReleasesUrl =
        "https://api.github.com/repos/REBUS-ORBIT/prism-agent/releases/latest";

    static readonly Version _currentVersion =
        typeof(Updater).Assembly.GetName().Version ?? new Version(0, 1, 0);

    // ------------------------------------------------------------------

    public sealed record UpdateInfo(string TagName, string DownloadUrl, Version NewVersion);

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

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets) &&
            assets.ValueKind == JsonValueKind.Array &&
            assets.GetArrayLength() > 0)
        {
            downloadUrl = assets[0].TryGetProperty("browser_download_url", out var bu)
                ? bu.GetString()
                : null;
        }

        if (!Version.TryParse(tagName.TrimStart('v'), out var newVersion))
            return null;

        return newVersion > _currentVersion
            ? new UpdateInfo(tagName, downloadUrl ?? "", newVersion)
            : null;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Downloads the update zip, then launches a background PowerShell script
    /// that waits for this process to exit, extracts the zip over the install
    /// directory, and relaunches the agent.
    /// Calls <see cref="Application.Exit"/> after scheduling the script.
    /// </summary>
    public static async Task DownloadAndInstallAsync(UpdateInfo info, IProgress<int> progress)
    {
        if (string.IsNullOrEmpty(info.DownloadUrl))
            throw new InvalidOperationException("No download URL in the release.");

        var tempZip = Path.Combine(Path.GetTempPath(), "PRISM.Agent.Update.zip");

        // --- Download ---
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"PRISM.Agent/{_currentVersion} (Windows)");

        using var resp = await http.GetAsync(
            info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength ?? 0;
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

        // Single-quoted strings inside the PS script escape ' as ''.
        var ps = $@"
$proc = Get-Process -Id {pid} -ErrorAction SilentlyContinue
if ($proc) {{ $proc.WaitForExit(60000) }}
Start-Sleep -Milliseconds 500
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory('{Esc(tempZip)}', '{Esc(installDir)}', $true)
if (Test-Path '{Esc(exePath)}') {{ Start-Process '{Esc(exePath)}' }}
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
        });

        Application.Exit();
    }

    static string Esc(string path) => path.Replace("'", "''");
}
