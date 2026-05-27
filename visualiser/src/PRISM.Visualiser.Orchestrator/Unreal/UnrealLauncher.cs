using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

using Serilog;

using PRISM.Visualiser.Orchestrator.Process;

namespace PRISM.Visualiser.Orchestrator.Unreal;

/// <summary>
/// Spawns <c>UnrealEditor-Cmd.exe</c> against a scaffolded per-run
/// project, runs the rendered <c>import_orbit.py</c> via
/// <c>-run=PythonScript</c>, and parses the ready / error marker lines
/// the python script emits to stdout.
///
/// <para>
/// Marker contract (mirrors <c>import_orbit.py</c>'s end-of-script
/// emission):
/// <code>
///   PRISM_VISUALISER_READY {"runId":"...","levelPath":"...", "assetCount":42, "importDurationMs":12345}
///   PRISM_VISUALISER_ERROR {"code":"import_failed","message":"..."}
/// </code>
/// Either marker terminates the wait. The python script <c>sys.exit</c>s
/// with a non-zero code on the error path so the editor returns a
/// non-zero exit code as well; the launcher cross-checks this.
/// </para>
///
/// <para>
/// All UE stdout is forwarded line-by-line to Serilog under the
/// <c>ue-editor</c> channel (warnings on stderr) so logs survive even
/// if the import never reaches a marker.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnrealLauncher
{
    /// <summary>Marker prefix the python ready emission uses.</summary>
    public const string ReadyMarkerPrefix = "PRISM_VISUALISER_READY ";

    /// <summary>Marker prefix the python error emission uses.</summary>
    public const string ErrorMarkerPrefix = "PRISM_VISUALISER_ERROR ";

    /// <summary>Default wait budget per the plan (10 min).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Channel string used when forwarding UE stdout to Serilog.</summary>
    public const string LogChannel = "ue-editor";

    private readonly UnrealInstall _install;
    private readonly JobObject _job;
    private readonly ILogger _log;

    public UnrealLauncher(UnrealInstall install, JobObject job, ILogger log)
    {
        _install = install ?? throw new ArgumentNullException(nameof(install));
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Launch UE for an import run. Blocks until the python script emits
    /// a ready / error marker, the process exits, or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    public async Task<UnrealImportResult> LaunchImportAsync(
        ScaffoldResult scaffold,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scaffold);
        timeout ??= DefaultTimeout;

        var psi = BuildStartInfoCore(_install, scaffold);
        _log.Information(
            "ue launch project={Project} python={Python} timeoutMs={TimeoutMs}",
            scaffold.UprojectPath, scaffold.PythonScriptPath, (int)timeout.Value.TotalMilliseconds);

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        var readyTcs = new TaskCompletionSource<UnrealReadyMarker>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var errorTcs = new TaskCompletionSource<UnrealErrorMarker>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var ueChannel = _log.ForContext("channel", LogChannel);
        process.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (line is null) return;
            ueChannel.Information("{Line}", line);
            TryConsumeMarker(line, readyTcs, errorTcs);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (line is null) return;
            ueChannel.Warning("{Line}", line);
            // UE often double-prints log lines on stderr — markers are
            // valid here too. Mirror the stdout consumer.
            TryConsumeMarker(line, readyTcs, errorTcs);
        };

        var startedAt = DateTime.UtcNow;
        if (!process.Start())
        {
            throw new UnrealLaunchException(
                $"Failed to start UnrealEditor-Cmd.exe at '{_install.EditorCmdPath}'.");
        }

        try
        {
            _job.AddProcess(process.Id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex,
                "ue launch: failed to add UE pid={Pid} to JobObject; KILL_ON_JOB_CLOSE will not cover it.",
                process.Id);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for either marker, the process exiting, the cancellation
        // token, or the configured timeout — whichever fires first.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout.Value);

        var exitTask = process.WaitForExitAsync(timeoutCts.Token);

        // Task.WhenAny never throws; it surfaces the first task to
        // complete (including a Canceled exitTask). Discriminating
        // outcomes happens on the awaiting side below.
        var winner = await Task.WhenAny(
            readyTcs.Task,
            errorTcs.Task,
            exitTask).ConfigureAwait(false);

        // Even if a marker fired, drain the editor exit so Job Object
        // cleanup completes deterministically.
        if (winner == readyTcs.Task)
        {
            var ready = await readyTcs.Task.ConfigureAwait(false);
            await WaitForExitOrKillAsync(process, timeout.Value, timeoutCts.Token).ConfigureAwait(false);
            var elapsed = DateTime.UtcNow - startedAt;
            _log.Information(
                "ue import ready runId={RunId} level={Level} assets={AssetCount} elapsedMs={ElapsedMs}",
                ready.RunId, ready.LevelPath, ready.AssetCount, (long)elapsed.TotalMilliseconds);
            return new UnrealImportResult(
                Status: UnrealImportStatus.Ready,
                Marker: ready,
                ExitCode: SafeExitCode(process),
                ProcessId: process.Id,
                Elapsed: elapsed,
                Error: null);
        }

        if (winner == errorTcs.Task)
        {
            var err = await errorTcs.Task.ConfigureAwait(false);
            await WaitForExitOrKillAsync(process, timeout.Value, timeoutCts.Token).ConfigureAwait(false);
            var elapsed = DateTime.UtcNow - startedAt;
            _log.Error(
                "ue import error code={Code} message={Message} elapsedMs={ElapsedMs}",
                err.Code, err.Message, (long)elapsed.TotalMilliseconds);
            return new UnrealImportResult(
                Status: UnrealImportStatus.PythonError,
                Marker: null,
                ExitCode: SafeExitCode(process),
                ProcessId: process.Id,
                Elapsed: elapsed,
                Error: err);
        }

        // Process exited without emitting a marker. That's a real
        // failure — the editor either crashed (non-zero exit code) or
        // the python script bailed before our try/except got to print
        // its error marker (zero exit code with no marker).
        if (winner == exitTask)
        {
            var elapsed = DateTime.UtcNow - startedAt;
            // The exit task completed because the process ended,
            // possibly because the linked timeout fired and
            // process.WaitForExitAsync threw — handle both cases here.
            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new UnrealLaunchTimeoutException(
                    $"UE import did not emit a ready marker within {timeout.Value.TotalSeconds:F0}s.");
            }
            try
            {
                await exitTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new UnrealLaunchTimeoutException(
                    $"UE import did not emit a ready marker within {timeout.Value.TotalSeconds:F0}s.");
            }
            var exitCode = SafeExitCode(process);
            _log.Error(
                "ue import exited without ready marker exit={Exit} elapsedMs={ElapsedMs}",
                exitCode, (long)elapsed.TotalMilliseconds);
            return new UnrealImportResult(
                Status: UnrealImportStatus.NoMarker,
                Marker: null,
                ExitCode: exitCode,
                ProcessId: process.Id,
                Elapsed: elapsed,
                Error: null);
        }

        throw new InvalidOperationException("Unreachable: Task.WhenAny returned an unknown task.");
    }

    /// <summary>
    /// Build the <see cref="ProcessStartInfo"/> the launcher uses. Public
    /// + static so tests can assert the argument string without spawning UE.
    /// </summary>
    public static ProcessStartInfo BuildStartInfoForTest(
        UnrealInstall install, ScaffoldResult scaffold)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(scaffold);
        return BuildStartInfoCore(install, scaffold);
    }

    private static ProcessStartInfo BuildStartInfoCore(UnrealInstall install, ScaffoldResult scaffold)
    {
        var psi = new ProcessStartInfo
        {
            FileName = install.EditorCmdPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = scaffold.ProjectRoot,
        };

        // Argument order matches the canonical UE 5.7 commandlet form:
        //   UnrealEditor-Cmd.exe <project.uproject> -run=PythonScript -ExecutePythonScript=<script.py>
        // Headless / unattended flags suppress splash + interactive prompts.
        psi.ArgumentList.Add(scaffold.UprojectPath);
        psi.ArgumentList.Add("-run=PythonScript");
        psi.ArgumentList.Add(string.Format(
            CultureInfo.InvariantCulture,
            "-ExecutePythonScript={0}", scaffold.PythonScriptPath));
        psi.ArgumentList.Add("-Unattended");
        psi.ArgumentList.Add("-NoSplash");
        psi.ArgumentList.Add("-NoPause");
        psi.ArgumentList.Add("-NullRHI");
        psi.ArgumentList.Add("-stdout");
        psi.ArgumentList.Add("-FullStdOutLogOutput");

        return psi;
    }

    // ----------------------------------------------------------------
    // Phase F — -game / Pixel Streaming launch
    // ----------------------------------------------------------------

    /// <summary>Default resolution the game-mode launcher requests.</summary>
    public const int DefaultGameResX = 1920;

    /// <summary>Default resolution the game-mode launcher requests.</summary>
    public const int DefaultGameResY = 1080;

    /// <summary>
    /// Launch UE in <c>-game</c> mode with PixelStreaming flags. Returns
    /// a handle wrapping the started <see cref="System.Diagnostics.Process"/>
    /// — the caller is responsible for awaiting the streamer-connected
    /// event on the Cirrus side (UE itself doesn't emit a structured
    /// ready marker in -game mode) and for tearing the process down
    /// when the run ends.
    ///
    /// <para>
    /// Why <c>UnrealEditor-Cmd.exe</c> for <c>-game</c> mode (and not the
    /// "Win32 GUI subsystem" <c>UnrealEditor.exe</c>):
    /// </para>
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>UnrealEditor-Cmd.exe</c> and <c>UnrealEditor.exe</c> are
    ///     the same Unreal monolith linked against different Win32
    ///     subsystems. The <c>-Cmd</c> build uses the Console subsystem,
    ///     so its stdout / stderr are inherited by us cleanly via
    ///     <see cref="ProcessStartInfo.RedirectStandardOutput"/>.
    ///     <c>UnrealEditor.exe</c> attaches to a Windows GUI subsystem;
    ///     its <c>log.AddLogListener(stdout)</c> path is unreliable
    ///     when launched from a non-console parent like our test
    ///     harness.
    ///   </description></item>
    ///   <item><description>
    ///     <c>-game</c> mode is fully supported by the <c>-Cmd</c>
    ///     binary — Unreal's command-line dispatch picks the
    ///     <c>UGameEngine</c> path based on the <c>-game</c> switch,
    ///     not on which subsystem the binary was linked against.
    ///     Pixel Streaming 2 examples in the UE 5.7 docs use both
    ///     binaries interchangeably; we standardise on <c>-Cmd</c>
    ///     because we already have its path resolved from Phase E.
    ///   </description></item>
    ///   <item><description>
    ///     PS2 still creates a real D3D12 device + NVENC encoder when
    ///     <c>-RenderOffScreen</c> is set: the offscreen flag bypasses
    ///     window presentation but the GPU pipeline is fully active.
    ///     We intentionally do NOT pass <c>-NullRHI</c> for the
    ///     game-mode launch (it would disable the very RHI that
    ///     drives the streamer).
    ///   </description></item>
    /// </list>
    /// </summary>
    public UnrealGameHandle LaunchGameMode(
        ScaffoldResult scaffold,
        string signallingUrl,
        string streamerId,
        int resX = DefaultGameResX,
        int resY = DefaultGameResY)
    {
        ArgumentNullException.ThrowIfNull(scaffold);
        ArgumentException.ThrowIfNullOrWhiteSpace(signallingUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamerId);

        var psi = BuildGameStartInfoCore(_install, scaffold, signallingUrl, streamerId, resX, resY);
        _log.Information(
            "ue game launch project={Project} level={Level} signallingUrl={SignallingUrl} streamerId={StreamerId} res={ResX}x{ResY}",
            scaffold.UprojectPath, scaffold.LevelPath, signallingUrl, streamerId, resX, resY);

        var process = new System.Diagnostics.Process { StartInfo = psi };
        var ueChannel = _log.ForContext("channel", "ue-game");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) ueChannel.Information("{Line}", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) ueChannel.Warning("{Line}", e.Data);
        };

        if (!process.Start())
        {
            throw new UnrealLaunchException(
                $"Failed to start UnrealEditor-Cmd.exe (-game mode) at '{_install.EditorCmdPath}'.");
        }

        try
        {
            _job.AddProcess(process.Id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex,
                "ue game launch: failed to add UE pid={Pid} to JobObject; " +
                "KILL_ON_JOB_CLOSE will not cover it.", process.Id);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new UnrealGameHandle(_log, process, streamerId);
    }

    /// <summary>
    /// Build the <see cref="ProcessStartInfo"/> the game-mode launcher
    /// uses. Public + static so tests can assert the argument string
    /// without spawning UE.
    /// </summary>
    public static ProcessStartInfo BuildGameStartInfoForTest(
        UnrealInstall install,
        ScaffoldResult scaffold,
        string signallingUrl,
        string streamerId,
        int resX = DefaultGameResX,
        int resY = DefaultGameResY)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(scaffold);
        return BuildGameStartInfoCore(install, scaffold, signallingUrl, streamerId, resX, resY);
    }

    private static ProcessStartInfo BuildGameStartInfoCore(
        UnrealInstall install,
        ScaffoldResult scaffold,
        string signallingUrl,
        string streamerId,
        int resX,
        int resY)
    {
        var psi = new ProcessStartInfo
        {
            FileName = install.EditorCmdPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = scaffold.ProjectRoot,
        };

        // Canonical UE 5.7 PixelStreaming 2 -game invocation:
        //   UnrealEditor-Cmd.exe <project>.uproject <levelPath> -game \
        //     -RenderOffScreen -ResX=1920 -ResY=1080 \
        //     -PixelStreamingURL=ws://localhost:<port> \
        //     -PixelStreamingID=<streamerId> \
        //     -log -unattended
        //
        // PS2 (UE 5.5+) deprecated the legacy -PixelStreamingIP /
        // -PixelStreamingPort pair in favour of -PixelStreamingURL.
        // The plan §Risks calls this out and the UE 5.7 docs are
        // explicit: do NOT mix the two forms.
        psi.ArgumentList.Add(scaffold.UprojectPath);
        psi.ArgumentList.Add(scaffold.LevelPath);
        psi.ArgumentList.Add("-game");
        psi.ArgumentList.Add("-RenderOffScreen");
        psi.ArgumentList.Add(string.Format(CultureInfo.InvariantCulture, "-ResX={0}", resX));
        psi.ArgumentList.Add(string.Format(CultureInfo.InvariantCulture, "-ResY={0}", resY));
        psi.ArgumentList.Add(string.Format(
            CultureInfo.InvariantCulture, "-PixelStreamingURL={0}", signallingUrl));
        psi.ArgumentList.Add(string.Format(
            CultureInfo.InvariantCulture, "-PixelStreamingID={0}", streamerId));
        psi.ArgumentList.Add("-Unattended");
        psi.ArgumentList.Add("-NoSplash");
        psi.ArgumentList.Add("-NoPause");
        psi.ArgumentList.Add("-stdout");
        psi.ArgumentList.Add("-FullStdOutLogOutput");
        psi.ArgumentList.Add("-log");

        return psi;
    }

    /// <summary>
    /// Parse one stdout / stderr line. Public + static so tests can
    /// assert marker recognition without spawning UE.
    /// </summary>
    public static MarkerParseResult ParseLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.StartsWith(ReadyMarkerPrefix, StringComparison.Ordinal))
        {
            var json = line[ReadyMarkerPrefix.Length..].Trim();
            try
            {
                var marker = JsonSerializer.Deserialize(
                    json, UnrealMarkerJsonContext.Default.UnrealReadyMarker)
                    ?? throw new UnrealLaunchException(
                        $"Empty ready-marker payload: '{line}'.");
                return MarkerParseResult.Ready(marker);
            }
            catch (JsonException ex)
            {
                throw new UnrealLaunchException(
                    $"Malformed ready-marker payload: '{line}'.", ex);
            }
        }
        if (line.StartsWith(ErrorMarkerPrefix, StringComparison.Ordinal))
        {
            var json = line[ErrorMarkerPrefix.Length..].Trim();
            try
            {
                var marker = JsonSerializer.Deserialize(
                    json, UnrealMarkerJsonContext.Default.UnrealErrorMarker)
                    ?? throw new UnrealLaunchException(
                        $"Empty error-marker payload: '{line}'.");
                return MarkerParseResult.Error(marker);
            }
            catch (JsonException ex)
            {
                throw new UnrealLaunchException(
                    $"Malformed error-marker payload: '{line}'.", ex);
            }
        }
        return MarkerParseResult.NoMarker();
    }

    private static void TryConsumeMarker(
        string line,
        TaskCompletionSource<UnrealReadyMarker> readyTcs,
        TaskCompletionSource<UnrealErrorMarker> errorTcs)
    {
        try
        {
            var parsed = ParseLine(line);
            switch (parsed.Kind)
            {
                case MarkerKind.Ready:
                    readyTcs.TrySetResult(parsed.ReadyMarker!);
                    break;
                case MarkerKind.Error:
                    errorTcs.TrySetResult(parsed.ErrorMarker!);
                    break;
                case MarkerKind.None:
                default:
                    break;
            }
        }
        catch (UnrealLaunchException ex)
        {
            // A malformed marker still counts as an error; surface it
            // via the error TCS so the launcher returns deterministically.
            errorTcs.TrySetResult(new UnrealErrorMarker("malformed_marker", ex.Message));
        }
    }

    private static async Task WaitForExitOrKillAsync(
        System.Diagnostics.Process process, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Min(30, timeout.TotalSeconds)));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Editor sometimes hangs on shutdown — KILL_ON_JOB_CLOSE
            // will eventually catch it, but be assertive about cleanup.
            await KillProcessTreeAsync(process).ConfigureAwait(false);
        }
    }

    private static async Task KillProcessTreeAsync(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort — JobObject KILL_ON_JOB_CLOSE is the safety net.
        }
    }

    private static int SafeExitCode(System.Diagnostics.Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }
}

/// <summary>Outcome of a single launcher run.</summary>
public sealed record UnrealImportResult(
    UnrealImportStatus Status,
    UnrealReadyMarker? Marker,
    int ExitCode,
    int ProcessId,
    TimeSpan Elapsed,
    UnrealErrorMarker? Error);

public enum UnrealImportStatus
{
    Ready,
    PythonError,
    NoMarker,
}

/// <summary>JSON-bound payload of a <c>PRISM_VISUALISER_READY</c> line.</summary>
public sealed record UnrealReadyMarker(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("levelPath")] string LevelPath,
    [property: JsonPropertyName("assetCount")] int AssetCount,
    [property: JsonPropertyName("importDurationMs")] int ImportDurationMs);

/// <summary>JSON-bound payload of a <c>PRISM_VISUALISER_ERROR</c> line.</summary>
public sealed record UnrealErrorMarker(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>One line of UE stdout, classified.</summary>
public sealed class MarkerParseResult
{
    public MarkerKind Kind { get; }
    public UnrealReadyMarker? ReadyMarker { get; }
    public UnrealErrorMarker? ErrorMarker { get; }

    private MarkerParseResult(MarkerKind kind, UnrealReadyMarker? ready, UnrealErrorMarker? error)
    {
        Kind = kind; ReadyMarker = ready; ErrorMarker = error;
    }
    public static MarkerParseResult NoMarker() => new(MarkerKind.None, null, null);
    public static MarkerParseResult Ready(UnrealReadyMarker m) => new(MarkerKind.Ready, m, null);
    public static MarkerParseResult Error(UnrealErrorMarker m) => new(MarkerKind.Error, null, m);
}

public enum MarkerKind { None, Ready, Error }

/// <summary>Source-generated JSON context for marker payloads.</summary>
[JsonSerializable(typeof(UnrealReadyMarker))]
[JsonSerializable(typeof(UnrealErrorMarker))]
internal sealed partial class UnrealMarkerJsonContext : JsonSerializerContext { }

/// <summary>UE failed to start or returned a malformed marker.</summary>
public sealed class UnrealLaunchException : Exception
{
    public UnrealLaunchException(string message) : base(message) { }
    public UnrealLaunchException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>UE didn't emit a marker within the configured timeout.</summary>
public sealed class UnrealLaunchTimeoutException : Exception
{
    public UnrealLaunchTimeoutException(string message) : base(message) { }
}

/// <summary>
/// Handle to a running UE <c>-game</c> process. Phase F composes this
/// with a <see cref="PixelStreaming.SignallingHandle"/> in
/// <c>PixelStreamingSession</c>; the session waits for the
/// "Streamer connected" event on the Cirrus side before declaring the
/// run ready, and tears UE down before Cirrus on shutdown.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UnrealGameHandle : IAsyncDisposable
{
    private readonly Serilog.ILogger _log;
    private readonly System.Diagnostics.Process _process;
    private bool _disposed;

    internal UnrealGameHandle(Serilog.ILogger log, System.Diagnostics.Process process, string streamerId)
    {
        _log = log;
        _process = process;
        StreamerId = streamerId;
    }

    /// <summary>Streamer id passed to UE on the command line.</summary>
    public string StreamerId { get; }

    /// <summary>PID of the running UE child process.</summary>
    public int ProcessId => _process.Id;

    /// <summary>True if the child process has exited.</summary>
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>Latest reported exit code, or -1 if the process is still running.</summary>
    public int ExitCode
    {
        get
        {
            try { return _process.HasExited ? _process.ExitCode : -1; }
            catch { return -1; }
        }
    }

    /// <summary>Block until the UE process exits.</summary>
    public Task WaitForExitAsync(CancellationToken ct) =>
        _process.WaitForExitAsync(ct);

    /// <summary>Kill the UE process (process tree).</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ue game kill failed pid={Pid}", _process.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Kill();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
        finally
        {
            _process.Dispose();
        }
    }
}
