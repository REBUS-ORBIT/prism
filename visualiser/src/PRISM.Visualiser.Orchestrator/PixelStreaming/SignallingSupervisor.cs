using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Serilog;

using PRISM.Visualiser.Orchestrator.Process;

namespace PRISM.Visualiser.Orchestrator.PixelStreaming;

/// <summary>
/// Locates the PixelStreaming 2 Cirrus signalling server under a UE
/// install, spawns it via Node, and parses its stdout for the "ready"
/// line that announces the WebSocket listener has come up.
///
/// <para>
/// Layout the supervisor expects under the UE root:
/// <list type="bullet">
///   <item><description>
///     Cirrus script:
///     <c>Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\</c>
///     containing one of <c>Cirrus.js</c> / <c>cirrus.js</c> /
///     <c>main.js</c> / <c>server.js</c> / <c>index.js</c>.
///   </description></item>
///   <item><description>
///     Bundled Node runtime:
///     <c>Engine\Binaries\ThirdParty\Node\Win64\node.exe</c>.
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// Both paths can be overridden via env vars for local smoke testing
/// without a full UE install:
/// <list type="bullet">
///   <item><description><c>PRISM_VISUALISER_CIRRUS_SCRIPT</c> — absolute path to the JS entrypoint.</description></item>
///   <item><description><c>PRISM_VISUALISER_NODE_EXE</c> — absolute path to <c>node.exe</c>.</description></item>
/// </list>
/// </para>
///
/// <para>
/// The ready-line regex is permissive — PS2's signalling server has
/// shipped at least three log shapes across UE 5.5 / 5.6 / 5.7
/// (<c>WebSocketServer started, listening on port 8888</c>,
/// <c>Listening on :8888</c>, <c>HTTP server listening on port 8888</c>).
/// We accept any line that contains a "listen" verb and a port number
/// matching the one we asked Cirrus to bind to.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SignallingSupervisor
{
    /// <summary>Default budget for Cirrus to log its ready line.</summary>
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Serilog channel used for forwarded Cirrus stdout / stderr.</summary>
    public const string LogChannel = "cirrus";

    /// <summary>Sub-path under the UE root that holds the signalling server.</summary>
    public const string SignallingWebServerRelative =
        @"Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer";

    /// <summary>Sub-path under the UE root for the bundled Node runtime.</summary>
    public const string NodeExeRelative =
        @"Engine\Binaries\ThirdParty\Node\Win64\node.exe";

    /// <summary>Env var the smoke test uses to point at a fake Cirrus script.</summary>
    public const string EnvVarCirrusScript = "PRISM_VISUALISER_CIRRUS_SCRIPT";

    /// <summary>Env var the smoke test uses to point at a custom Node binary.</summary>
    public const string EnvVarNodeExe = "PRISM_VISUALISER_NODE_EXE";

    /// <summary>Candidate filenames for the Cirrus entrypoint, in resolution order.</summary>
    public static readonly IReadOnlyList<string> CirrusScriptCandidates = new[]
    {
        "Cirrus.js",
        "cirrus.js",
        "main.js",
        "server.js",
        "index.js",
    };

    /// <summary>
    /// Permissive ready-line regex. Captures the listening port so the
    /// caller can sanity-check the port Cirrus actually bound to (in
    /// case it ignored the <c>--HttpPort</c> flag).
    /// </summary>
    public static readonly Regex ReadyLinePattern = new(
        @"(?ix)
          (?:listening\s+on(?:\s+port)?[\s:]*|started.*listening.*?port\s*)
          (?<port>\d{2,5})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex matching the "streamer connected" log line Cirrus prints
    /// once UE's WebRTC streamer registers. Captures the streamer id.
    /// </summary>
    public static readonly Regex StreamerConnectedPattern = new(
        @"(?ix)
          streamer\s+(?:connected|registered)[\s:]+(?<id>[\w\-]+)",
        RegexOptions.Compiled);

    private readonly ILogger _log;
    private readonly JobObject _job;

    public SignallingSupervisor(ILogger log, JobObject job)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _job = job ?? throw new ArgumentNullException(nameof(job));
    }

    /// <summary>
    /// Resolve the Cirrus script + Node binary the supervisor will
    /// invoke. Env-var overrides take precedence over the canonical UE
    /// install paths. Returns <see langword="null"/> for either part
    /// the supervisor can't locate; the caller maps that to a
    /// <c>signalling_not_found</c> / <c>node_not_found</c> failure
    /// event.
    /// </summary>
    public static SignallingResolveResult Resolve(string ueRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ueRoot);

        var scriptOverride = Environment.GetEnvironmentVariable(EnvVarCirrusScript);
        var nodeOverride = Environment.GetEnvironmentVariable(EnvVarNodeExe);

        string? script = null;
        if (!string.IsNullOrWhiteSpace(scriptOverride) && File.Exists(scriptOverride))
        {
            script = scriptOverride;
        }
        else
        {
            var webServerDir = Path.Combine(ueRoot, SignallingWebServerRelative);
            if (Directory.Exists(webServerDir))
            {
                foreach (var candidate in CirrusScriptCandidates)
                {
                    var path = Path.Combine(webServerDir, candidate);
                    if (File.Exists(path))
                    {
                        script = path;
                        break;
                    }
                }
            }
        }

        string? node = null;
        if (!string.IsNullOrWhiteSpace(nodeOverride) && File.Exists(nodeOverride))
        {
            node = nodeOverride;
        }
        else
        {
            var bundled = Path.Combine(ueRoot, NodeExeRelative);
            if (File.Exists(bundled)) node = bundled;
        }

        return new SignallingResolveResult(
            CirrusScriptPath: script,
            NodeExePath: node);
    }

    /// <summary>
    /// Spawn Cirrus and wait for its ready line. The returned handle
    /// owns the child process; <see cref="SignallingHandle.Kill"/> or
    /// <see cref="SignallingHandle.DisposeAsync"/> tears it down.
    /// </summary>
    public async Task<SignallingHandle> StartAsync(
        SignallingResolveResult resolved,
        int tcpPort,
        TimeSpan? readyTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (resolved.CirrusScriptPath is null)
        {
            throw new SignallingNotFoundException(
                "Cirrus signalling script could not be located under the UE root " +
                "(expected PixelStreaming2 plugin to ship it).");
        }
        if (resolved.NodeExePath is null)
        {
            throw new NodeNotFoundException(
                "node.exe not found under the UE root " +
                $"({NodeExeRelative}); the bundled UE node runtime is missing.");
        }
        if (tcpPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(tcpPort));

        readyTimeout ??= DefaultReadyTimeout;

        var psi = BuildStartInfo(resolved, tcpPort);
        _log.Information(
            "cirrus launch script={Script} node={Node} port={Port} timeoutMs={TimeoutMs}",
            resolved.CirrusScriptPath, resolved.NodeExePath, tcpPort,
            (int)readyTimeout.Value.TotalMilliseconds);

        var process = new System.Diagnostics.Process { StartInfo = psi };

        // Channel buffers every stdout / stderr line so multiple
        // consumers (ready-line watcher + later streamer-connected
        // watcher) can re-read the stream without losing events.
        var lineChannel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        var cirrusChannel = _log.ForContext("channel", LogChannel);

        process.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (line is null) return;
            cirrusChannel.Information("{Line}", line);
            lineChannel.Writer.TryWrite(line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (line is null) return;
            // Node logs many "informational" lines on stderr — treat
            // them at the same level so the ready-line parser doesn't
            // miss them just because Node printed them on stderr.
            cirrusChannel.Warning("{Line}", line);
            lineChannel.Writer.TryWrite(line);
        };
        process.Exited += (_, _) =>
        {
            try { lineChannel.Writer.TryComplete(); } catch { /* already completed */ }
        };
        process.EnableRaisingEvents = true;

        if (!process.Start())
        {
            throw new SignallingStartException(
                $"Failed to start Cirrus via Node at '{resolved.NodeExePath}'.");
        }

        try
        {
            _job.AddProcess(process.Id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex,
                "cirrus launch: failed to add Cirrus pid={Pid} to JobObject; " +
                "KILL_ON_JOB_CLOSE will not cover it.", process.Id);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Watch the line channel for the ready line. Tasks for the
        // process exiting and the timeout race in parallel.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(readyTimeout.Value);

        try
        {
            var port = await AwaitReadyAsync(
                ReadChannelLines(lineChannel.Reader, timeoutCts.Token),
                timeoutCts.Token).ConfigureAwait(false);
            if (port > 0 && port != tcpPort)
            {
                _log.Warning(
                    "cirrus ready: requested port={Requested} but log reported port={Logged}",
                    tcpPort, port);
            }
            return new SignallingHandle(_log, process, lineChannel, tcpPort);
        }
        catch (OperationCanceledException) when (
            timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await KillProcessQuietlyAsync(process).ConfigureAwait(false);
            throw new SignallingStartTimeoutException(
                $"Cirrus did not log a ready line within {readyTimeout.Value.TotalSeconds:F0}s.");
        }
        catch
        {
            await KillProcessQuietlyAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Consume <paramref name="lines"/> until a Cirrus ready-line is
    /// observed; return the parsed port (or 0 if the line didn't
    /// contain one — we still treat the line as ready for the
    /// permissive PS2 message shape). Throws
    /// <see cref="OperationCanceledException"/> when the stream
    /// completes before a ready line shows up.
    /// </summary>
    public static async Task<int> AwaitReadyAsync(
        IAsyncEnumerable<string> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        await foreach (var line in lines.WithCancellation(ct).ConfigureAwait(false))
        {
            if (TryParseReadyLine(line, out int port))
            {
                return port;
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new SignallingStartException(
            "Cirrus stdout closed before emitting a ready line.");
    }

    /// <summary>
    /// Consume <paramref name="lines"/> until a "Streamer connected"
    /// line is observed; return the streamer id. Throws
    /// <see cref="OperationCanceledException"/> on timeout / cancellation
    /// or <see cref="UeGameStartTimeoutException"/> when the stream
    /// completes without a match.
    /// </summary>
    public static async Task<string> AwaitStreamerConnectedAsync(
        IAsyncEnumerable<string> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        await foreach (var line in lines.WithCancellation(ct).ConfigureAwait(false))
        {
            if (TryParseStreamerConnected(line, out var streamerId))
            {
                return streamerId;
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new UeGameStartTimeoutException(
            "Cirrus stdout closed before a streamer connected.");
    }

    /// <summary>
    /// Parse one Cirrus stdout line. Returns true (and the port) when
    /// the line matches <see cref="ReadyLinePattern"/>; false otherwise.
    /// </summary>
    public static bool TryParseReadyLine(string line, out int port)
    {
        port = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var match = ReadyLinePattern.Match(line);
        if (!match.Success) return false;
        var portText = match.Groups["port"].Value;
        if (int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            port = parsed;
        }
        return true;
    }

    /// <summary>
    /// Parse one Cirrus stdout line for the "streamer connected" event.
    /// Returns true (and the streamer id) on match; false otherwise.
    /// </summary>
    public static bool TryParseStreamerConnected(string line, out string streamerId)
    {
        streamerId = string.Empty;
        if (string.IsNullOrEmpty(line)) return false;
        var match = StreamerConnectedPattern.Match(line);
        if (!match.Success) return false;
        streamerId = match.Groups["id"].Value;
        return true;
    }

    private static ProcessStartInfo BuildStartInfo(SignallingResolveResult resolved, int tcpPort)
    {
        var workingDir = Path.GetDirectoryName(resolved.CirrusScriptPath!)
            ?? AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = resolved.NodeExePath!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        psi.ArgumentList.Add(resolved.CirrusScriptPath!);
        // Cirrus accepts --HttpPort=8888 (and --StreamerPort=8889, etc.).
        // We only pin the HTTP/WS port; the other side falls back to
        // the defaults baked into the PS2 server config.
        psi.ArgumentList.Add(string.Format(
            CultureInfo.InvariantCulture, "--HttpPort={0}", tcpPort));
        return psi;
    }

    private static async IAsyncEnumerable<string> ReadChannelLines(
        ChannelReader<string> reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var line))
            {
                yield return line;
            }
        }
    }

    private static async Task KillProcessQuietlyAsync(System.Diagnostics.Process process)
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
}

/// <summary>Result of <see cref="SignallingSupervisor.Resolve"/>.</summary>
public sealed record SignallingResolveResult(
    string? CirrusScriptPath,
    string? NodeExePath)
{
    /// <summary>True when both the script and node binary were found.</summary>
    public bool IsComplete => CirrusScriptPath is not null && NodeExePath is not null;
}

/// <summary>
/// Handle to a running Cirrus signalling server. Owns the child
/// process and exposes the shared stdout / stderr line channel so
/// later consumers can watch for the streamer-connected event without
/// re-attaching to <see cref="System.Diagnostics.Process.OutputDataReceived"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SignallingHandle : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly System.Diagnostics.Process _process;
    private readonly Channel<string> _lineChannel;
    private bool _disposed;

    internal SignallingHandle(
        ILogger log,
        System.Diagnostics.Process process,
        Channel<string> lineChannel,
        int tcpPort)
    {
        _log = log;
        _process = process;
        _lineChannel = lineChannel;
        TcpPort = tcpPort;
    }

    /// <summary>The TCP port Cirrus was told to bind to.</summary>
    public int TcpPort { get; }

    /// <summary>PID of the running Cirrus child process.</summary>
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

    /// <summary>Subscribed live stream of remaining Cirrus log lines.</summary>
    public ChannelReader<string> Lines => _lineChannel.Reader;

    /// <summary>
    /// Wait for the Cirrus process to exit. Surfaces the exit code via
    /// the returned task.
    /// </summary>
    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        try { return _process.ExitCode; }
        catch { return -1; }
    }

    /// <summary>Kill the Cirrus process (process tree).</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "cirrus kill failed pid={Pid}", _process.Id);
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
            try { _lineChannel.Writer.TryComplete(); } catch { /* already complete */ }
            _process.Dispose();
        }
    }
}

/// <summary>Cirrus script could not be located under the UE root.</summary>
public sealed class SignallingNotFoundException : Exception
{
    public SignallingNotFoundException(string message) : base(message) { }
}

/// <summary>UE's bundled node.exe could not be located.</summary>
public sealed class NodeNotFoundException : Exception
{
    public NodeNotFoundException(string message) : base(message) { }
}

/// <summary>Cirrus failed to start (process spawn error).</summary>
public sealed class SignallingStartException : Exception
{
    public SignallingStartException(string message) : base(message) { }
    public SignallingStartException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Cirrus didn't log a ready line within the budget.</summary>
public sealed class SignallingStartTimeoutException : Exception
{
    public SignallingStartTimeoutException(string message) : base(message) { }
}

/// <summary>UE -game mode didn't register a streamer within the budget.</summary>
public sealed class UeGameStartTimeoutException : Exception
{
    public UeGameStartTimeoutException(string message) : base(message) { }
}
