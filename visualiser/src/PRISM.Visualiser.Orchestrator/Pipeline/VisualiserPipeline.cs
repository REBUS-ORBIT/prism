using System.Runtime.Versioning;

using Serilog;

using PRISM.Visualiser.Orchestrator.Auth;
using PRISM.Visualiser.Orchestrator.Cache;
using PRISM.Visualiser.Orchestrator.Converters.FromOrbit;
using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.OrbitApi;
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
}

/// <summary>Output of <see cref="VisualiserPipeline.ReceiveAndStageAsync"/>.</summary>
public sealed record StageOutcome(StagedEvent StagedEvent, string GltfPath);

/// <summary>Output of <see cref="VisualiserPipeline.ImportAsync"/>.</summary>
public sealed record ImportResult(
    ImportedEvent ImportedEvent,
    ScaffoldResult Scaffold,
    int ProcessId,
    TimeSpan Elapsed);
