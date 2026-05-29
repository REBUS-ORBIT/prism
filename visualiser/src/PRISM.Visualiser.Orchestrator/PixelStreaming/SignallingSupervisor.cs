using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using Serilog;

using PRISM.Visualiser.Orchestrator.Process;

namespace PRISM.Visualiser.Orchestrator.PixelStreaming;

/// <summary>
/// Locates the PixelStreaming signalling server under a UE install,
/// spawns it via Node, and parses its stdout for the "ready" line
/// that announces the WebSocket listener has come up.
///
/// <para>
/// PS2 (UE 5.5+) replaced the original "Cirrus" signalling server
/// with "Wilbur" (<c>@epicgames-ps/wilbur</c>), a TypeScript app
/// compiled to <c>SignallingWebServer\dist\index.js</c>. The old
/// Cirrus path (a single top-level <c>cirrus.js</c>) is kept as a
/// fallback for older PS1 plugin variants we don't formally support
/// but might still encounter on customer workstations.
/// </para>
///
/// <para>
/// Layout the supervisor expects under the UE root:
/// <list type="bullet">
///   <item><description>
///     <b>Wilbur (preferred — PS2, UE 5.5+):</b>
///     <c>Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\dist\index.js</c>
///     populated by <see cref="SignallingBootstrap"/> running
///     <c>get_ps_servers.bat</c> + <c>start.bat</c>.
///   </description></item>
///   <item><description>
///     <b>Cirrus (fallback — pre-5.5):</b>
///     <c>Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\</c>
///     containing one of <c>Cirrus.js</c> / <c>cirrus.js</c> /
///     <c>main.js</c> / <c>server.js</c> / <c>index.js</c>.
///   </description></item>
///   <item><description>
///     <b>Bundled Node runtime (Wilbur):</b>
///     <c>Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\platform_scripts\cmd\node\node.exe</c>
///     downloaded by <c>start.bat</c> during bootstrap.
///   </description></item>
///   <item><description>
///     <b>Bundled Node runtime (Cirrus fallback):</b>
///     <c>Engine\Binaries\ThirdParty\Node\Win64\node.exe</c>.
///   </description></item>
/// </list>
/// </para>
///
/// <para>
/// All four paths can be overridden via env vars for local smoke
/// testing without a full UE install:
/// <list type="bullet">
///   <item><description><c>PRISM_VISUALISER_CIRRUS_SCRIPT</c> — absolute path to the JS entrypoint (wilbur OR cirrus).</description></item>
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
/// matching the one we asked Wilbur to bind to.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SignallingSupervisor
{
    /// <summary>Default budget for the signalling server to log its ready line.</summary>
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Serilog channel used for forwarded signalling stdout / stderr.</summary>
    public const string LogChannel = "cirrus";

    /// <summary>Sub-path under the UE root that holds the signalling server.</summary>
    public const string SignallingWebServerRelative =
        @"Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer";

    /// <summary>
    /// Sub-path under the UE root of the Wilbur signalling-server
    /// entrypoint (UE 5.5+). Produced by
    /// <see cref="SignallingBootstrap"/> on first run.
    /// </summary>
    public const string WilburEntrypointRelative =
        @"Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\dist\index.js";

    /// <summary>
    /// Sub-path under the UE root of the Node runtime downloaded by
    /// <c>start.bat</c> during the Wilbur bootstrap. We prefer this
    /// over the engine-bundled Node (which may not exist on UE 5.7
    /// launcher installs) so that <c>node --version</c> on disk
    /// matches the version that built wilbur.
    /// </summary>
    public const string WilburNodeExeRelative =
        @"Engine\Plugins\Media\PixelStreaming2\Resources\WebServers\SignallingWebServer\platform_scripts\cmd\node\node.exe";

    /// <summary>Sub-path under the UE root for the bundled Node runtime (legacy PS1).</summary>
    public const string NodeExeRelative =
        @"Engine\Binaries\ThirdParty\Node\Win64\node.exe";

    /// <summary>Env var the smoke test uses to point at a fake Cirrus script.</summary>
    public const string EnvVarCirrusScript = "PRISM_VISUALISER_CIRRUS_SCRIPT";

    /// <summary>Env var the smoke test uses to point at a custom Node binary.</summary>
    public const string EnvVarNodeExe = "PRISM_VISUALISER_NODE_EXE";

    /// <summary>
    /// Candidate filenames for the legacy Cirrus entrypoint, in
    /// resolution order. Only consulted when the Wilbur entrypoint at
    /// <see cref="WilburEntrypointRelative"/> is missing.
    /// </summary>
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
    /// caller can sanity-check the port the signalling server actually
    /// bound to (in case it ignored the port flag).
    ///
    /// <para>
    /// Wilbur (UE 5.5+) typically logs lines such as
    /// <c>HTTP webserver listening on port 8080</c> or
    /// <c>Started listening on port 8888</c>; the legacy Cirrus shape
    /// (<c>Listening on :8888</c>, <c>WebSocketServer started, listening on port 8888</c>)
    /// is also covered for back-compat.
    /// </para>
    /// </summary>
    public static readonly Regex ReadyLinePattern = new(
        @"(?ix)
          (?:listening\s+on(?:\s+port)?[\s:]*|started.*listening.*?port\s*|listen\s+on(?:\s+port)?[\s:]*)
          (?<port>\d{2,5})\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Named regex pattern used by <see cref="TryParseStreamerConnected"/>.
    /// The matcher tries each pattern in <see cref="StreamerConnectedPatterns"/>
    /// in order; the first match wins and its <see cref="Name"/> is
    /// reported via the diagnostic log line so future Epic / Wilbur log
    /// renames are easy to attribute.
    /// </summary>
    public sealed record NamedStreamerPattern(string Name, Regex Pattern);

    /// <summary>
    /// Ordered list of regex patterns recognising the
    /// "UE streamer registered with the signalling server" event in
    /// PixelStreaming 2 (UE 5.5+ / Wilbur).
    ///
    /// <para>
    /// Up to v0.3.8 the supervisor only matched the legacy Cirrus
    /// shape (<c>Streamer connected: orbit_abc123</c>). UE 5.7 + Wilbur
    /// dropped that line entirely; the orchestrator hit
    /// <c>ue_game_start_timeout</c> at 120s on every PC01 run even
    /// when the handshake completed cleanly within ~23 s. v0.3.9
    /// switches to a multi-pattern matcher that fires on ANY of the
    /// four signals UE 5.7 + Wilbur emit when the streamer registers.
    /// </para>
    ///
    /// <para>
    /// Order matters — entries are evaluated front-to-back per line
    /// and the first hit returns. The canonical "streamer registered"
    /// event in PS2 is the UE-side
    /// <c>RoomSignallingContextObserver::OnJoined ... state=[Joined]</c>
    /// log line; the others are Wilbur-side / simpler-shape alternates
    /// kept so the matcher degrades gracefully when Epic renames a
    /// channel in a future point release.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item><description>
    ///     <b><c>OnJoined</c></b> — canonical UE-side event, captured from
    ///     UE's stdout. Example:
    ///     <c>LogPixelStreaming2EpicRtc: RoomSignallingContextObserver::OnJoined.
    ///     Local participant joined the room. roomId=[orbit_cb4d2125]
    ///     localParticipantId=[orbit_cb4d2125] state=[Joined]</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b><c>PlayerJoined</c></b> — simpler UE-side variant.
    ///     Example: <c>LogPixelStreaming2RTC: Player (orbit_cb4d2125) joined</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b><c>EndpointIdConfirm</c></b> — Wilbur acknowledges the
    ///     streamer-id the UE side proposed. Example:
    ///     <c>info: &lt; orbit_cb4d2125 :: {"type":"endpointIdConfirm","committedId":"orbit_cb4d2125"}</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b><c>EndpointId</c></b> — Wilbur observes UE introduce its
    ///     id. Example:
    ///     <c>info: &gt; UnknownStreamer :: {"id":"orbit_cb4d2125",...,"type":"endpointId"}</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b><c>LegacyCirrus</c></b> — pre-PS2 fallback for any
    ///     environment still on the old Cirrus signalling server.
    ///     Example: <c>Streamer connected: orbit_abc123</c>.
    ///   </description></item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlyList<NamedStreamerPattern> StreamerConnectedPatterns = new[]
    {
        // Canonical UE-side `OnJoined` event WITH `localParticipantId=[...]`.
        // PS2 (UE 5.7) emits a single line of the shape:
        //   LogPixelStreaming2EpicRtc: RoomSignallingContextObserver::OnJoined.
        //   Local participant joined the room. roomId=[orbit_<id>]
        //   localParticipantId=[orbit_<id>] state=[Joined]
        // The id appears as `localParticipantId=[orbit_<id>]` BEFORE
        // `state=[Joined]`. We require all three tokens so a hypothetical
        // future log line that mentions `OnJoined` somewhere else
        // (e.g. an "OnJoined: [Joining]" telemetry event) can't false-positive.
        new NamedStreamerPattern(
            "OnJoined",
            new Regex(
                @"(?ix)
                  LogPixelStreaming2EpicRtc
                  .*?RoomSignallingContextObserver::OnJoined
                  .*?localParticipantId=\[(?<id>[^\]]+)\]
                  .*?state=\[Joined\]",
                RegexOptions.Compiled)),
        // Fallback when UE drops `localParticipantId=[...]` from the log
        // (or moves it past `state=[Joined]`). Same canonical signal —
        // the empty id surfaces as `(none)` in the diagnostic line.
        new NamedStreamerPattern(
            "OnJoined",
            new Regex(
                @"(?ix)
                  LogPixelStreaming2EpicRtc
                  .*?RoomSignallingContextObserver::OnJoined
                  .*?state=\[Joined\]",
                RegexOptions.Compiled)),
        new NamedStreamerPattern(
            "PlayerJoined",
            new Regex(
                @"(?ix)
                  LogPixelStreaming2RTC
                  .*?Player\s*\(\s*(?<id>[^)]+?)\s*\)\s+joined",
                RegexOptions.Compiled)),
        new NamedStreamerPattern(
            "EndpointIdConfirm",
            new Regex(
                @"endpointIdConfirm.*?""committedId""\s*:\s*""(?<id>[^""]+)""",
                RegexOptions.Compiled)),
        new NamedStreamerPattern(
            "EndpointId",
            new Regex(
                @"UnknownStreamer.*?""id""\s*:\s*""(?<id>[^""]+)"".*?""type""\s*:\s*""endpointId""",
                RegexOptions.Compiled)),
        new NamedStreamerPattern(
            "LegacyCirrus",
            new Regex(
                @"(?ix)
                  streamer\s+(?:connected|registered)(?:\s+with\s+id)?[\s:]+
                  (?<id>[\w\-]+)",
                RegexOptions.Compiled)),
    };

    private readonly ILogger _log;
    private readonly JobObject _job;

    public SignallingSupervisor(ILogger log, JobObject job)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _job = job ?? throw new ArgumentNullException(nameof(job));
    }

    /// <summary>
    /// Resolve the signalling-server script + Node binary the
    /// supervisor will invoke. Env-var overrides take precedence over
    /// the canonical UE install paths. Resolution order:
    /// <list type="number">
    ///   <item><description>Env override (<see cref="EnvVarCirrusScript"/>).</description></item>
    ///   <item><description>Wilbur <c>dist\index.js</c> (PS2, UE 5.5+).</description></item>
    ///   <item><description>Legacy Cirrus candidates (pre-5.5 / community plugins).</description></item>
    /// </list>
    /// Returns <see langword="null"/> for either part the supervisor
    /// can't locate; the caller maps that to a
    /// <c>signalling_not_found</c> / <c>node_not_found</c> failure
    /// event. The returned <see cref="SignallingResolveResult.IsWilbur"/>
    /// flag tells the caller which CLI dialect to use when launching.
    /// </summary>
    public static SignallingResolveResult Resolve(string ueRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ueRoot);

        var scriptOverride = Environment.GetEnvironmentVariable(EnvVarCirrusScript);
        var nodeOverride = Environment.GetEnvironmentVariable(EnvVarNodeExe);

        string? script = null;
        bool isWilbur = false;
        var probedPaths = new List<string>(capacity: 8);

        if (!string.IsNullOrWhiteSpace(scriptOverride) && File.Exists(scriptOverride))
        {
            script = scriptOverride;
            // The override is treated as Wilbur if it lives under a
            // SignallingWebServer\dist tree — otherwise we assume the
            // legacy Cirrus CLI dialect.
            isWilbur = scriptOverride.Replace('/', '\\')
                .Contains(@"\dist\", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // 1. Prefer Wilbur (UE 5.5+) over legacy Cirrus candidates.
            var wilburPath = Path.Combine(ueRoot, WilburEntrypointRelative);
            probedPaths.Add(wilburPath);
            if (File.Exists(wilburPath))
            {
                script = wilburPath;
                isWilbur = true;
            }
            else
            {
                // 2. Fall back to top-level Cirrus candidates for pre-5.5
                //    plugin variants.
                var webServerDir = Path.Combine(ueRoot, SignallingWebServerRelative);
                if (Directory.Exists(webServerDir))
                {
                    foreach (var candidate in CirrusScriptCandidates)
                    {
                        var path = Path.Combine(webServerDir, candidate);
                        probedPaths.Add(path);
                        if (File.Exists(path))
                        {
                            script = path;
                            isWilbur = false;
                            break;
                        }
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
            // Prefer the wilbur-bundled Node when present (it matches
            // the version that built wilbur). Fall back to the engine
            // ThirdParty Node only when the wilbur node tree isn't
            // there (legacy Cirrus path).
            var wilburNode = Path.Combine(ueRoot, WilburNodeExeRelative);
            if (File.Exists(wilburNode))
            {
                node = wilburNode;
            }
            else
            {
                var bundled = Path.Combine(ueRoot, NodeExeRelative);
                if (File.Exists(bundled)) node = bundled;
            }
        }

        return new SignallingResolveResult(
            CirrusScriptPath: script,
            NodeExePath: node,
            IsWilbur: isWilbur,
            ProbedPaths: probedPaths);
    }

    /// <summary>
    /// Spawn the signalling server (Wilbur on PS2, Cirrus on legacy
    /// PS1) and wait for its ready line. The returned handle owns the
    /// child process; <see cref="SignallingHandle.Kill"/> or
    /// <see cref="SignallingHandle.DisposeAsync"/> tears it down.
    /// </summary>
    /// <param name="resolved">Output of <see cref="Resolve"/>.</param>
    /// <param name="playerPort">
    ///   TCP port for client / player traffic (HTTP + player WS). On
    ///   Wilbur this is the <c>--player_port</c> argument; on legacy
    ///   Cirrus this is the single <c>--HttpPort</c> the supervisor
    ///   pinned. Surfaced to the agent as the loopback player URL.
    /// </param>
    /// <param name="streamerPort">
    ///   TCP port the UE streamer process connects to. Required for
    ///   Wilbur (<c>--streamer_port</c>); ignored on legacy Cirrus
    ///   where the streamer and player traffic share one port.
    /// </param>
    public async Task<SignallingHandle> StartAsync(
        SignallingResolveResult resolved,
        int playerPort,
        int streamerPort,
        TimeSpan? readyTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (resolved.CirrusScriptPath is null)
        {
            throw new SignallingNotFoundException(
                "PixelStreaming signalling server entrypoint could not be located. " +
                "Expected wilbur at " +
                $"'{WilburEntrypointRelative}' under the UE root, or a legacy " +
                $"Cirrus script under '{SignallingWebServerRelative}'.");
        }
        if (resolved.NodeExePath is null)
        {
            throw new NodeNotFoundException(
                "node.exe not found under the UE root. Probed " +
                $"'{WilburNodeExeRelative}' (wilbur bundle) and " +
                $"'{NodeExeRelative}' (legacy engine bundle).");
        }
        if (playerPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(playerPort));
        if (streamerPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(streamerPort));

        readyTimeout ??= DefaultReadyTimeout;

        var psi = BuildStartInfo(resolved, playerPort, streamerPort);
        _log.Information(
            "signalling launch flavour={Flavour} script={Script} node={Node} " +
            "playerPort={PlayerPort} streamerPort={StreamerPort} timeoutMs={TimeoutMs}",
            resolved.IsWilbur ? "wilbur" : "cirrus",
            resolved.CirrusScriptPath, resolved.NodeExePath,
            playerPort, streamerPort,
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
            // Wilbur logs lines for both player_port AND streamer_port;
            // we only care that the player_port we asked for showed up.
            if (port > 0 && port != playerPort && port != streamerPort)
            {
                _log.Warning(
                    "signalling ready: requested player={Player} streamer={Streamer} but log reported port={Logged}",
                    playerPort, streamerPort, port);
            }
            return new SignallingHandle(_log, process, lineChannel, playerPort, streamerPort);
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
    /// Consume <paramref name="lines"/> until a "streamer registered"
    /// line is observed; return the streamer id and the
    /// <see cref="NamedStreamerPattern.Name"/> of the pattern that
    /// fired. Throws <see cref="OperationCanceledException"/> on
    /// timeout / cancellation or <see cref="UeGameStartTimeoutException"/>
    /// when the stream completes without a match.
    /// </summary>
    public static async Task<StreamerConnectedMatch> AwaitStreamerConnectedAsync(
        IAsyncEnumerable<string> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lines);
        await foreach (var line in lines.WithCancellation(ct).ConfigureAwait(false))
        {
            if (TryParseStreamerConnected(line, out var streamerId, out var patternName))
            {
                return new StreamerConnectedMatch(streamerId, patternName);
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new UeGameStartTimeoutException(
            "Signalling stdout closed before a streamer connected.");
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
    /// Parse one signalling / UE stdout line for the
    /// "UE streamer registered" event. Tries each pattern in
    /// <see cref="StreamerConnectedPatterns"/> in order; the first
    /// match wins. On match returns true and emits the streamer id +
    /// the <see cref="NamedStreamerPattern.Name"/> of the pattern that
    /// fired so the orchestrator can attribute the diagnostic log line.
    /// </summary>
    public static bool TryParseStreamerConnected(
        string line, out string streamerId, out string matchedPattern)
    {
        streamerId = string.Empty;
        matchedPattern = string.Empty;
        if (string.IsNullOrEmpty(line)) return false;
        foreach (var named in StreamerConnectedPatterns)
        {
            var match = named.Pattern.Match(line);
            if (!match.Success) continue;
            // Some patterns (e.g. OnJoined without a `localParticipantId=[...]` tail)
            // won't capture an id group — that's still a valid registration
            // signal; surface an empty id rather than rejecting the line.
            streamerId = match.Groups["id"].Success ? match.Groups["id"].Value : string.Empty;
            matchedPattern = named.Name;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Build the <see cref="ProcessStartInfo"/> the supervisor uses to
    /// spawn the signalling server. Two CLI dialects are supported:
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Wilbur (PS2):</b> uses <c>commander</c>-style
    ///     <c>--player_port N --streamer_port M --serve
    ///     --console_messages verbose --log_config</c>. Working
    ///     directory must be the wilbur package root
    ///     (<c>SignallingWebServer\</c>) so <c>config.json</c> is
    ///     picked up.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Legacy Cirrus (PS1):</b> uses <c>--HttpPort=N</c>. The
    ///     streamer port is implicit (single port for player + streamer).
    ///   </description></item>
    /// </list>
    /// Public so tests can pin the exact ArgumentList shape per
    /// dialect without spawning a real process.
    /// </summary>
    public static ProcessStartInfo BuildStartInfo(
        SignallingResolveResult resolved, int playerPort, int streamerPort)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (resolved.CirrusScriptPath is null)
        {
            throw new InvalidOperationException(
                "BuildStartInfo called with a null CirrusScriptPath; the " +
                "Resolve probe should have surfaced signalling_not_found first.");
        }

        // Wilbur lives at <pkg-root>\dist\index.js; we want the cwd
        // set to <pkg-root> so relative paths in wilbur's config.json
        // (e.g. <c>"http_root": "www"</c>) resolve correctly.
        string workingDir;
        if (resolved.IsWilbur)
        {
            // dist\index.js → parent is "dist", grandparent is the
            // wilbur package root (SignallingWebServer).
            var dist = Path.GetDirectoryName(resolved.CirrusScriptPath);
            workingDir = (dist is not null
                ? Path.GetDirectoryName(dist)
                : null) ?? AppContext.BaseDirectory;
        }
        else
        {
            workingDir = Path.GetDirectoryName(resolved.CirrusScriptPath)
                ?? AppContext.BaseDirectory;
        }

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

        if (resolved.IsWilbur)
        {
            // Wilbur (UE 5.5+) CLI shape — see
            // SignallingWebServer/src/index.ts.
            psi.ArgumentList.Add(string.Format(
                CultureInfo.InvariantCulture, "--player_port={0}", playerPort));
            psi.ArgumentList.Add(string.Format(
                CultureInfo.InvariantCulture, "--streamer_port={0}", streamerPort));
            psi.ArgumentList.Add("--serve");
            psi.ArgumentList.Add("--console_messages");
            psi.ArgumentList.Add("verbose");
            psi.ArgumentList.Add("--log_config");
        }
        else
        {
            // Legacy Cirrus (pre-5.5) CLI shape. Cirrus accepts
            // --HttpPort=8888 (and --StreamerPort=8889, etc.). We
            // only pin the HTTP/WS port; the other side falls back
            // to the defaults baked into the PS2 server config.
            psi.ArgumentList.Add(string.Format(
                CultureInfo.InvariantCulture, "--HttpPort={0}", playerPort));
        }
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

/// <summary>
/// Result of <see cref="SignallingSupervisor.Resolve"/>.
/// </summary>
/// <param name="CirrusScriptPath">
///   Absolute path of the signalling-server entrypoint (wilbur
///   <c>dist\index.js</c> when <see cref="IsWilbur"/> is true; a
///   legacy Cirrus JS file otherwise). Null when neither path probed.
/// </param>
/// <param name="NodeExePath">Absolute path to the Node binary we'll launch the script with.</param>
/// <param name="IsWilbur">
///   True when the resolved script is wilbur (UE 5.5+ PS2). Selects
///   the CLI dialect <see cref="SignallingSupervisor.BuildStartInfo"/>
///   emits.
/// </param>
/// <param name="ProbedPaths">
///   Absolute paths the resolver inspected, in probe order, for
///   diagnostic logging when both branches missed. Empty when an
///   env-var override short-circuited the resolution.
/// </param>
public sealed record SignallingResolveResult(
    string? CirrusScriptPath,
    string? NodeExePath,
    bool IsWilbur = false,
    IReadOnlyList<string>? ProbedPaths = null)
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
        int playerPort,
        int streamerPort)
    {
        _log = log;
        _process = process;
        _lineChannel = lineChannel;
        TcpPort = playerPort;
        StreamerPort = streamerPort;
    }

    /// <summary>
    /// The TCP port the player (browser / HTTP / player-WS) side
    /// listens on. Alias of <see cref="PlayerPort"/>, kept for
    /// back-compat with the pre-Wilbur supervisor where this was the
    /// single port the server bound to.
    /// </summary>
    public int TcpPort { get; }

    /// <summary>The TCP port the player (browser / HTTP) connects to.</summary>
    public int PlayerPort => TcpPort;

    /// <summary>The TCP port UE's WebRTC streamer connects to.</summary>
    public int StreamerPort { get; }

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

/// <summary>
/// Result of a successful streamer-connected line match. Carries both
/// the streamer id (when the matching pattern captured one) and the
/// name of the pattern that fired so the orchestrator's diagnostic
/// log line can attribute the event to a specific Wilbur / UE shape.
/// </summary>
/// <param name="StreamerId">
///   The streamer id captured from the matched line. May be the empty
///   string when the matched pattern doesn't carry an id group (e.g.
///   the minimal <c>OnJoined</c> shape without a
///   <c>localParticipantId=[...]</c> tail).
/// </param>
/// <param name="MatchedPattern">
///   <see cref="SignallingSupervisor.NamedStreamerPattern.Name"/> of the
///   pattern that fired — one of <c>OnJoined</c>, <c>PlayerJoined</c>,
///   <c>EndpointIdConfirm</c>, <c>EndpointId</c>, or <c>LegacyCirrus</c>.
/// </param>
public readonly record struct StreamerConnectedMatch(
    string StreamerId,
    string MatchedPattern);
