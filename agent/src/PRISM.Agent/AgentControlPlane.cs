using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using PRISM.Agent.Config;
using PRISM.Agent.Pipeline;
using PRISM.Agent.Tray;
using PRISM.Agent.Ws;
using PRISM.Contracts;

namespace PRISM.Agent;

/// <summary>
/// Live, mutable agent state shared between the tray UI and the web UI.
///
/// Every consumer that wants to "change a setting" or "pause the watcher"
/// goes through this object instead of poking <see cref="AgentConfig"/> /
/// <see cref="WsClient"/> directly, so the two surfaces stay in sync and
/// re-emitting <see cref="MessageType.Hello"/> after a mutation lives in
/// exactly one place.
/// </summary>
public sealed class AgentControlPlane
{
    readonly ILogger<AgentControlPlane> _log;
    readonly AgentConfig _cfg;
    readonly WsClient _ws;
    readonly WorkerSlotPool _slots;

    bool _paused;

    public AgentControlPlane(
        ILogger<AgentControlPlane> log,
        AgentConfig cfg,
        WsClient ws,
        WorkerSlotPool slots)
    {
        _log = log;
        _cfg = cfg;
        _ws = ws;
        _slots = slots;
    }

    // ---- Read-only views ------------------------------------------------

    public AgentConfig Config => _cfg;

    public bool IsPaused => _paused;
    public bool IsConnected => _ws.IsConnected;

    public int SlotsBusy => _slots.BusyCount;
    public int SlotsTotal => _cfg.Slots;

    public string AgentVersion =>
        typeof(AgentControlPlane).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public IReadOnlyCollection<string> SupportedFormats => AgentService.SupportedFormats;

    /// <summary>Raised whenever a mutation runs.  Tray + web UI subscribe.</summary>
    public event Action? StateChanged;

    void Notify()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { _log.LogWarning(ex, "control-plane subscriber threw"); }
    }

    // ---- Watcher pause / resume ----------------------------------------

    public async Task PauseAsync()
    {
        if (_paused) return;
        _paused = true;
        try { await _ws.PauseAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "ws pause threw"); }
        _log.LogInformation("watcher paused");
        Notify();
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        try { _ws.Resume(); }
        catch (Exception ex) { _log.LogWarning(ex, "ws resume threw"); }
        _log.LogInformation("watcher resumed");
        Notify();
    }

    // ---- Setting mutations ---------------------------------------------

    public async Task SetSlotsAsync(int slots)
    {
        var clamped = Math.Max(1, Math.Min(8, slots));
        if (clamped == _cfg.Slots) return;
        _cfg.Slots = clamped;
        _cfg.Save();
        _log.LogInformation("slots changed -> {Slots}", clamped);
        await SendHelloAsync();
        Notify();
    }

    public async Task SetRolesAsync(AgentRole[] roles)
    {
        var deduped = roles.Distinct().ToArray();
        if (deduped.SequenceEqual(_cfg.Roles)) return;
        _cfg.Roles = deduped;
        _cfg.Save();
        _log.LogInformation("roles changed -> {Roles}", string.Join(",", deduped));
        await SendHelloAsync();
        Notify();
    }

    public async Task SetNodeNameAsync(string nodeName)
    {
        if (string.IsNullOrWhiteSpace(nodeName)) return;
        if (nodeName == _cfg.NodeName) return;
        _cfg.NodeName = nodeName.Trim();
        _cfg.Save();
        _log.LogInformation("nodeName changed -> {NodeName}", _cfg.NodeName);
        await SendHelloAsync();
        Notify();
    }

    /// <summary>
    /// Apply an arbitrary subset of settings in one shot.  Returns
    /// <c>RestartRequired = true</c> when a field changed that the running
    /// agent cannot pick up live (PrismUrl, RhinoVersion, WebUiPort,
    /// WebUiBindAll).
    /// </summary>
    public async Task<ConfigUpdateResult> ApplyAsync(ConfigUpdate update)
    {
        bool restart = false;

        if (update.PrismUrl is { } u && u != _cfg.PrismUrl)
        {
            _cfg.PrismUrl = u;
            restart = true;
        }
        if (update.RhinoVersion is { } rv && rv != _cfg.RhinoVersion)
        {
            _cfg.RhinoVersion = rv;
            restart = true;
        }
        if (update.WebUiPort is { } port && port != _cfg.WebUiPort)
        {
            _cfg.WebUiPort = port;
            restart = true;
        }
        if (update.WebUiBindAll is { } bindAll && bindAll != _cfg.WebUiBindAll)
        {
            _cfg.WebUiBindAll = bindAll;
            restart = true;
        }

        if (update.NodeName is { } name && !string.IsNullOrWhiteSpace(name))
            _cfg.NodeName = name.Trim();

        if (update.Slots is { } slots)
            _cfg.Slots = Math.Max(1, Math.Min(8, slots));

        if (update.Roles is { } roles)
            _cfg.Roles = roles.Distinct().ToArray();

        if (update.LogDir is { } ld && !string.IsNullOrWhiteSpace(ld))
            _cfg.LogDir = ld.Trim();

        // Visualiser settings (Phase A: live-applied, no restart required —
        // the orchestrator only reads them at the next startVisualisation).
        if (update.UnrealEngineRoot is { } uer && !string.IsNullOrWhiteSpace(uer))
            _cfg.UnrealEngineRoot = uer.Trim();
        if (update.UnrealTemplateTag is { } utt && !string.IsNullOrWhiteSpace(utt))
            _cfg.UnrealTemplateTag = utt.Trim();
        if (update.VisualiserMaxConcurrent is { } vmc)
            _cfg.VisualiserMaxConcurrent = Math.Max(1, Math.Min(4, vmc));
        if (update.VisualiserGpuCheck is { } vgc)
            _cfg.VisualiserGpuCheck = vgc;

        _cfg.Save();
        _log.LogInformation("config saved (restartRequired={Restart})", restart);

        if (!restart)
            await SendHelloAsync();

        Notify();
        return new ConfigUpdateResult(restart, _cfg);
    }

    public Task SendHelloAsync()
    {
        return _ws.SendAsync(MessageType.Hello, new HelloData
        {
            MachineId    = _cfg.MachineId,
            NodeName     = _cfg.NodeName,
            Slots        = _cfg.Slots,
            Roles        = _cfg.Roles,
            Formats      = AgentService.SupportedFormats,
            AgentVersion = AgentVersion,
            RhinoVersion = null,
        }).AsTask();
    }

    // ---- Remote management (restart / update) --------------------------

    /// <summary>
    /// Schedule a clean exit of the current agent process and a
    /// self-relaunch shortly afterwards.
    ///
    /// The Windows Scheduled Task installed by <c>install.ps1</c> already
    /// carries <c>RestartCount=3</c> / <c>RestartInterval=1m</c>, but
    /// that only fires on task FAILURE (non-zero exit). To handle clean
    /// admin-initiated restarts uniformly we spawn a tiny PowerShell
    /// helper that waits for our PID to exit and then relaunches the
    /// agent EXE — exactly the same pattern used by
    /// <see cref="Updater.DownloadAndInstallAsync"/>.
    /// </summary>
    public Task RestartAsync(string? reason = null)
    {
        _log.LogWarning("restart requested (reason={Reason})", reason ?? "<none>");

        var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var exePath    = Path.Combine(installDir, "PRISM.Agent.exe");
        var pid        = Environment.ProcessId;
        var logPath    = Path.Combine(Path.GetTempPath(), "PRISM.Agent.Restart.log");

        var ps = $@"
$ErrorActionPreference = 'SilentlyContinue'
$log = '{Esc(logPath)}'
function W($m) {{ Add-Content -Path $log -Value (""[$([DateTime]::Now.ToString('HH:mm:ss'))] "" + $m) }}
W 'restart helper started for pid {pid}'
$proc = Get-Process -Id {pid} -ErrorAction SilentlyContinue
if ($proc) {{
    $null = $proc.WaitForExit(60000)
    W 'agent exited'
}} else {{
    W 'agent already exited'
}}
Start-Sleep -Milliseconds 500
if (Test-Path '{Esc(exePath)}') {{
    W 'launching new agent'
    Start-Process -FilePath '{Esc(exePath)}'
    W 'launched'
}} else {{
    W ""ERROR: exe not found at '{Esc(exePath)}'""
}}
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed to schedule restart helper; exiting anyway and trusting Scheduled Task RestartCount");
        }

        // Give the WS pump a brief moment to flush any pending acks/logs,
        // then exit. We exit with a non-zero code so the Scheduled Task's
        // RestartCount also fires as a belt-and-braces fallback if the
        // PowerShell helper failed to launch.
        _ = Task.Run(async () =>
        {
            await Task.Delay(750);
            try { Environment.Exit(2); }
            catch { /* best effort */ }
        });

        return Task.CompletedTask;
    }

    public sealed record UpdateOutcome(
        bool    UpdateAvailable,
        string? Tag,
        bool    Downloading,
        string? Error,
        bool    AlreadyRunning = false);

    /// <summary>
    /// Wire the same code path as the tray menu's "Check for updates"
    /// into a programmatic call. If a newer release is available on
    /// GitHub Releases, kicks off
    /// <see cref="Updater.DownloadAndInstallAsync"/> in the background
    /// (it self-terminates the process when extraction is scheduled).
    /// Returns synchronously so the HTTP / WS caller can ack quickly.
    /// </summary>
    /// <remarks>
    /// v0.1.36: if a download is already in flight (local tray click
    /// raced a remote WS update or vice versa) the second caller gets
    /// <c>AlreadyRunning = true</c> and the in-flight attempt is left
    /// untouched. <see cref="Updater.IsUpdateInProgress"/> short-circuits
    /// before we even hit GitHub Releases so we don't waste a request.
    /// </remarks>
    public async Task<UpdateOutcome> CheckAndApplyUpdateAsync(string? pinnedTag = null)
    {
        _log.LogInformation("update requested (tag={Tag})", pinnedTag ?? "<latest>");

        if (Updater.IsUpdateInProgress)
        {
            _log.LogWarning(
                "update request ignored — another update is already in progress on this agent");
            return new UpdateOutcome(
                false, null, false,
                "Another update is already in progress on this agent.",
                AlreadyRunning: true);
        }

        Updater.UpdateInfo? info;
        try
        {
            info = await Updater.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "update check failed");
            return new UpdateOutcome(false, null, false, ex.Message);
        }

        if (info is null)
        {
            _log.LogInformation("no update available (current={Version})", AgentVersion);
            return new UpdateOutcome(false, null, false, null);
        }

        // pinnedTag is honoured advisory-only: the GitHub release latest
        // is what Updater fetches today. If the operator pinned a tag
        // that does not match latest we still proceed with what's
        // available — admins generally want "give me whatever is on
        // GitHub now", not "match exact tag or fail".
        var captured = info;
        _ = Task.Run(async () =>
        {
            var prog = new Progress<int>(_ => { /* nop on the WS path */ });
            try
            {
                await Updater.DownloadAndInstallAsync(captured, prog);
            }
            catch (InvalidOperationException ex)
            {
                // Race: another caller grabbed _updateGate between our
                // IsUpdateInProgress probe above and the await inside
                // DownloadAndInstallAsync. Treat as benign, not an error.
                _log.LogWarning(
                    "update download skipped — already running ({Reason})",
                    ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "update download/install failed");
            }
        });

        return new UpdateOutcome(true, captured.TagName, true, null);
    }

    static string Esc(string path) => path.Replace("'", "''");
}

/// <summary>
/// Whitelisted partial update payload accepted by
/// <see cref="AgentControlPlane.ApplyAsync"/>.  Everything is nullable so
/// the web UI can PATCH single fields.
/// </summary>
public sealed class ConfigUpdate
{
    public string?      PrismUrl     { get; set; }
    public string?      NodeName     { get; set; }
    public int?         Slots        { get; set; }
    public AgentRole[]? Roles        { get; set; }
    public string?      RhinoVersion { get; set; }
    public string?      LogDir       { get; set; }
    public int?         WebUiPort    { get; set; }
    public bool?        WebUiBindAll { get; set; }
    // Visualiser (Phase A — orchestrator binary lands in Phase F/G)
    public string?      UnrealEngineRoot        { get; set; }
    public string?      UnrealTemplateTag       { get; set; }
    public int?         VisualiserMaxConcurrent { get; set; }
    public bool?        VisualiserGpuCheck      { get; set; }
}

public sealed record ConfigUpdateResult(bool RestartRequired, AgentConfig Config);
