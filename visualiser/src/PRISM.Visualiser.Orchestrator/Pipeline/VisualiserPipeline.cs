using System.Runtime.Versioning;

using Serilog;

using PRISM.Visualiser.Orchestrator.Auth;
using PRISM.Visualiser.Orchestrator.Cache;
using PRISM.Visualiser.Orchestrator.Converters.FromOrbit;
using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.OrbitApi;
using PRISM.Visualiser.Orchestrator.PixelStreaming;
using PRISM.Visualiser.Orchestrator.Process;
using PRISM.Visualiser.Orchestrator.Staging;
using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator.Pipeline;

/// <summary>
/// End-to-end Phase E pipeline: receive from ORBIT → stage to glTF →
/// resolve UE install → fetch template → scaffold per-run project →
/// launch UE editor and run the python import.
///
/// <para>
/// This is the "stream up to but not including PixelStreaming" surface
/// Phase F will compose with the Cirrus + UE -game bring-up. The CLI
/// invokes the pipeline once per <c>stream</c> run; tests can mock
/// any stage by swapping the dependency-injected components.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VisualiserPipeline
{
    private readonly CompositeOrbitTokenSource _tokenSource;
    private readonly TemplateFetcher _templateFetcher;
    private readonly ProjectScaffolder _scaffolder;
    private readonly JobObject _job;
    private readonly ILogger _log;
    private readonly TimeSpan _ueTimeout;

    public VisualiserPipeline(
        CompositeOrbitTokenSource tokenSource,
        TemplateFetcher templateFetcher,
        ProjectScaffolder scaffolder,
        JobObject job,
        ILogger log,
        TimeSpan? ueTimeout = null)
    {
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _templateFetcher = templateFetcher ?? throw new ArgumentNullException(nameof(templateFetcher));
        _scaffolder = scaffolder ?? throw new ArgumentNullException(nameof(scaffolder));
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ueTimeout = ueTimeout ?? UnrealLauncher.DefaultTimeout;
    }

    /// <summary>
    /// Run the receive + stage step. Identical to the Phase C path —
    /// kept here so the CLI's two phases (Phase C-only emission of
    /// <c>staged/v1</c>, Phase E continuation into UE) share one code
    /// path. Returns the <see cref="StagedEvent"/> the caller emits to
    /// stdout plus the staged file paths the UE launcher needs.
    /// </summary>
    public async Task<StageOutcome> ReceiveAndStageAsync(
        RunManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var token = await _tokenSource.RequireTokenAsync(manifest.Server, ct).ConfigureAwait(false);
        _log.Information("auth: token resolved server={Server}", manifest.Server.Name);

        var cache = CacheRoot.ResolveDefault().EnsureCreated();
        var contentCache = new ContentAddressedCache(cache);
        using var orbitApi = HttpOrbitApi.Create(manifest.Server, token);
        var blobs = new BlobDownloader(orbitApi, contentCache, _log);

        var stageDir = Path.Combine(cache.Stage, manifest.RunId);
        Directory.CreateDirectory(stageDir);
        var unknownsPath = Path.Combine(stageDir, "unknown_objects.jsonl");
        var unknowns = new UnknownObjectSink(unknownsPath);

        var pipeline = new OrbitReceivePipeline(orbitApi, contentCache, blobs, unknowns, _log);
        var scene = await pipeline
            .ReceiveAsync(manifest.ProjectId, manifest.VersionId, ct)
            .ConfigureAwait(false);

        var flat = SceneFlattener.Flatten(scene);
        var writer = new GltfWriter(_log);
        var result = writer.Write(flat, stageDir);

        var staged = StagedEvent.For(
            runId: manifest.RunId,
            stagePath: stageDir,
            manifestPath: result.ManifestPath,
            gltfPath: result.GltfPath,
            objectCount: result.ObjectCount,
            meshCount: result.MeshCount,
            materialCount: result.MaterialCount,
            textureCount: result.TextureCount,
            unknownCount: scene.Unknowns.Count);

        return new StageOutcome(StagedEvent: staged, GltfPath: result.GltfPath);
    }

    /// <summary>
    /// Phase E continuation: take the staged glTF and drive the UE
    /// editor through Interchange import. Returns the
    /// <see cref="ImportedEvent"/> the caller emits to stdout.
    /// </summary>
    /// <exception cref="UnrealLaunchTimeoutException">
    ///   UE didn't emit a ready marker within
    ///   <see cref="UnrealLauncher.DefaultTimeout"/>.
    /// </exception>
    /// <exception cref="UnrealLaunchException">
    ///   UE failed to start, exited without a marker, or the python
    ///   script's error marker fired.
    /// </exception>
    public async Task<ImportResult> ImportAsync(
        RunManifest manifest,
        UnrealInstall install,
        string templateTag,
        string gltfPath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(gltfPath);

        var template = await _templateFetcher.FetchAsync(templateTag, ct).ConfigureAwait(false);
        _log.Information(
            "template fetch tag={Tag} fromCache={FromCache} sha256={Sha} path={Path}",
            template.Tag, template.FromCache, template.Sha256, template.ZipPath);

        var scaffold = _scaffolder.Scaffold(template, manifest.RunId, gltfPath);
        _log.Information(
            "scaffold ready project={Project} python={Python} level={Level}",
            scaffold.ProjectRoot, scaffold.PythonScriptPath, scaffold.LevelPath);

        var launcher = new UnrealLauncher(install, _job, _log);
        var run = await launcher.LaunchImportAsync(scaffold, _ueTimeout, ct).ConfigureAwait(false);

        return run.Status switch
        {
            UnrealImportStatus.Ready => new ImportResult(
                ImportedEvent: ImportedEvent.For(
                    runId: manifest.RunId,
                    projectPath: scaffold.UprojectPath,
                    levelPath: run.Marker?.LevelPath ?? scaffold.LevelPath,
                    importDurationMs: run.Marker?.ImportDurationMs ?? (long)run.Elapsed.TotalMilliseconds,
                    assetCount: run.Marker?.AssetCount ?? 0),
                Scaffold: scaffold,
                ProcessId: run.ProcessId,
                Elapsed: run.Elapsed),
            UnrealImportStatus.PythonError => throw new UnrealLaunchException(
                $"UE python emitted error: code={run.Error?.Code} message={run.Error?.Message}"),
            UnrealImportStatus.NoMarker => throw new UnrealLaunchException(
                $"UE exited without a ready marker (exit={run.ExitCode})."),
            _ => throw new InvalidOperationException(
                $"Unknown UnrealImportStatus: {run.Status}"),
        };
    }

    /// <summary>
    /// Phase F continuation: with the per-run project already imported,
    /// stand up the Cirrus signalling server + the UE game-mode
    /// streamer, wait for them to handshake, and return a live
    /// <see cref="PixelStreamingSession"/>. The caller owns the
    /// session lifetime: it's expected to emit the
    /// <c>prism-visualiser/ready/v1</c> JSON line, then call
    /// <see cref="PixelStreamingSession.RunUntilExitAsync"/> to block
    /// until either UE exits or the orchestrator-side
    /// <see cref="CancellationToken"/> trips.
    /// </summary>
    /// <exception cref="SignallingNotFoundException">
    ///   Cirrus script not present under the UE root.
    /// </exception>
    /// <exception cref="NodeNotFoundException">
    ///   <c>node.exe</c> not present under the UE root.
    /// </exception>
    /// <exception cref="SignallingStartTimeoutException">
    ///   Cirrus didn't log a ready line within 30 s.
    /// </exception>
    /// <exception cref="UeGameStartTimeoutException">
    ///   UE didn't register a streamer with Cirrus within 120 s.
    /// </exception>
    /// <exception cref="UnrealLaunchException">
    ///   UE failed to start, or exited before the streamer connected.
    /// </exception>
    public async Task<PixelStreamingSession> StartStreamingAsync(
        RunManifest manifest,
        UnrealInstall install,
        ScaffoldResult scaffold,
        TimeSpan? signallingReadyTimeout = null,
        TimeSpan? streamerConnectTimeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(scaffold);

        var streamerConnectBudget = streamerConnectTimeout
            ?? PixelStreamingSession.DefaultStreamerConnectTimeout;

        // 1. Resolve Cirrus + node.exe before we allocate ports —
        //    fail-fast keeps the error event surface clean.
        var resolved = SignallingSupervisor.Resolve(install.Root);
        if (resolved.CirrusScriptPath is null)
        {
            throw new SignallingNotFoundException(
                "Cirrus signalling script could not be located under " +
                $"'{install.Root}\\{SignallingSupervisor.SignallingWebServerRelative}'. " +
                "Is the PixelStreaming2 plugin installed?");
        }
        if (resolved.NodeExePath is null)
        {
            throw new NodeNotFoundException(
                "UE-bundled node.exe could not be located at " +
                $"'{install.Root}\\{SignallingSupervisor.NodeExeRelative}'.");
        }
        _log.Information(
            "phase-f: resolved cirrus={Cirrus} node={Node}",
            resolved.CirrusScriptPath, resolved.NodeExePath);

        // 2. Allocate the signalling port (honour the agent's hint
        //    opportunistically) and a UDP range for WebRTC. The UDP
        //    range isn't passed to UE today — PS2's WebRTC stack
        //    auto-allocates ports inside the system ephemeral range —
        //    but we reserve them for observability + future flag
        //    plumbing.
        var signallingPort = PortAllocator
            .AllocateTcpPortHonouringHint(manifest.SignallingPortHint);
        var webrtcPorts = PortAllocator.AllocateUdpPortRange();
        _log.Information(
            "phase-f: allocated signallingPort={Port} webrtcPorts=[{WebRtc}]",
            signallingPort, string.Join(",", webrtcPorts));

        // 3. Spawn Cirrus + wait for its ready line. On failure the
        //    supervisor kills the process before throwing, so we
        //    don't need a try/finally here.
        var cirrusSupervisor = new SignallingSupervisor(_log, _job);
        var cirrusHandle = await cirrusSupervisor
            .StartAsync(resolved, signallingPort, signallingReadyTimeout, ct)
            .ConfigureAwait(false);

        // 4. Spawn UE -game and watch Cirrus stdout for the
        //    "Streamer connected" line. We dispose Cirrus on any
        //    failure here so we don't leak the supervisor.
        UnrealGameHandle? ueHandle = null;
        try
        {
            var streamerId = "orbit_" + ShortId(manifest.RunId);
            var signallingUrl = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "ws://127.0.0.1:{0}", signallingPort);

            var launcher = new UnrealLauncher(install, _job, _log);
            ueHandle = launcher.LaunchGameMode(
                scaffold, signallingUrl, streamerId);

            await WaitForStreamerConnectedAsync(
                    cirrusHandle, ueHandle, streamerConnectBudget, ct)
                .ConfigureAwait(false);

            _log.Information(
                "phase-f: streamer connected pid={Pid} streamerId={StreamerId} port={Port}",
                ueHandle.ProcessId, streamerId, signallingPort);

            return new PixelStreamingSession(_log, ueHandle, cirrusHandle);
        }
        catch
        {
            if (ueHandle is not null)
            {
                await ueHandle.DisposeAsync().ConfigureAwait(false);
            }
            await cirrusHandle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Race the streamer-connected line against UE exiting prematurely
    /// and the configured timeout. UE exiting before the streamer
    /// registers is a hard failure (<see cref="UnrealLaunchException"/>
    /// with the <c>ue_game_crashed</c> code in the message).
    /// </summary>
    private static async Task WaitForStreamerConnectedAsync(
        SignallingHandle cirrus,
        UnrealGameHandle ue,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var streamerTask = SignallingSupervisor.AwaitStreamerConnectedAsync(
            ReadChannelLines(cirrus.Lines, timeoutCts.Token), timeoutCts.Token);
        var ueExitTask = ue.WaitForExitAsync(timeoutCts.Token);

        var winner = await Task.WhenAny(streamerTask, ueExitTask).ConfigureAwait(false);
        if (winner == streamerTask)
        {
            try
            {
                _ = await streamerTask.ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (
                timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new UeGameStartTimeoutException(
                    $"UE did not register a streamer with Cirrus within {timeout.TotalSeconds:F0}s.");
            }
        }

        // UE exited before the streamer connected. Either it crashed
        // (non-zero exit) or it never reached PS2 init.
        if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new UeGameStartTimeoutException(
                $"UE did not register a streamer with Cirrus within {timeout.TotalSeconds:F0}s.");
        }
        ct.ThrowIfCancellationRequested();
        throw new UnrealLaunchException(
            $"UE -game exited (code={ue.ExitCode}) before registering a streamer with Cirrus.");
    }

    private static async IAsyncEnumerable<string> ReadChannelLines(
        System.Threading.Channels.ChannelReader<string> reader,
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

    private static string ShortId(string runId)
    {
        var trimmed = runId.Replace("-", string.Empty, StringComparison.Ordinal);
        return trimmed.Length >= 8 ? trimmed[..8] : trimmed;
    }
}

/// <summary>Output of <see cref="VisualiserPipeline.ReceiveAndStageAsync"/>.</summary>
public sealed record StageOutcome(StagedEvent StagedEvent, string GltfPath);

/// <summary>Output of <see cref="VisualiserPipeline.ImportAsync"/>.</summary>
public sealed record ImportResult(
    ImportedEvent ImportedEvent,
    ScaffoldResult Scaffold,
    int ProcessId,
    TimeSpan Elapsed);
