using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Runtime.Versioning;

using PRISM.Visualiser.Orchestrator.Auth;
using PRISM.Visualiser.Orchestrator.Cache;
using PRISM.Visualiser.Orchestrator.Converters.FromOrbit;
using PRISM.Visualiser.Orchestrator.Ipc;
using PRISM.Visualiser.Orchestrator.Logging;
using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.OrbitApi;
using PRISM.Visualiser.Orchestrator.Pipeline;
using PRISM.Visualiser.Orchestrator.Process;
using PRISM.Visualiser.Orchestrator.Staging;
using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator;

/// <summary>
/// CLI entrypoint. Two subcommands today:
///   stream  - launch a Pixel-Streaming session for one ORBIT version
///   cache   - cache management (prune, stat). Phase B only stubs `prune`.
///
/// All long-running work owns a Win32 Job Object created in Main, so
/// any future child process (Cirrus, UE) we spawn dies with us. The
/// JobObject is created BEFORE any subcommand handler runs so even an
/// `--unknown-flag` crash path stays clean.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Program
{
    /// <summary>
    /// Process-lifetime Job Object. Intentionally kept as a static field
    /// (never disposed) because explicitly closing the handle triggers
    /// <c>KILL_ON_JOB_CLOSE</c>, which terminates this very process with
    /// the OS-default exit code 0 — destroying any non-zero exit code we
    /// were about to return. The Windows kernel reclaims the handle when
    /// the process exits, and at that point the job has no live
    /// processes left so <c>KILL_ON_JOB_CLOSE</c> only affects children.
    /// </summary>
    private static JobObject? _processJob;

    public static async Task<int> Main(string[] args)
    {
        _processJob = JobObject.CreateAndAssignSelf();

        // System.CommandLine 2.0-beta4's default parser reports parse
        // errors via stderr but historically swallowed the resulting
        // exit code in some shells. Drive the parser explicitly so we
        // control both the error message and the exit code: any parse
        // error (unknown command, missing required option, bad enum
        // value) exits with code 64 — the BSD/sysexits convention for
        // EX_USAGE.
        var root = BuildRootCommand();
        var parser = new CommandLineBuilder(root)
            .UseHelp()
            .UseVersionOption()
            .UseTypoCorrections()
            .CancelOnProcessTermination()
            .Build();

        var parseResult = parser.Parse(args);
        if (parseResult.Errors.Count > 0)
        {
            foreach (var error in parseResult.Errors)
            {
                Console.Error.WriteLine($"error: {error.Message}");
            }
            Console.Error.WriteLine();
            Console.Error.WriteLine("Run with --help for usage.");
            return ExitCodes.Usage;
        }

        // If args parse cleanly but the user didn't pick a subcommand
        // (or picked the root command's --help / --version), help has
        // already printed. Make that flow obviously visible: we still
        // exit 0 for `--help`, but exit non-zero when the root command
        // is invoked with no actionable subcommand.
        if (parseResult.CommandResult.Command == root)
        {
            // --help / --version already wrote to stdout; bare invocation
            // (zero args) is a usage error.
            if (args.Length == 0)
            {
                Console.Error.WriteLine("error: a subcommand is required.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Run with --help for usage.");
                return ExitCodes.Usage;
            }
            return ExitCodes.Success;
        }

        return await parseResult.InvokeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Exit codes used by the orchestrator. Public so the agent and
    /// tests can reference them by name.
    ///
    /// <list type="table">
    ///   <listheader>
    ///     <term>Code</term><description>Meaning</description>
    ///   </listheader>
    ///   <item><term>0</term>  <description>Success (dry-run only at this phase)</description></item>
    ///   <item><term>1</term>  <description>Generic runtime failure</description></item>
    ///   <item><term>4</term>  <description>UE root not found</description></item>
    ///   <item><term>5</term>  <description>UE import timed out</description></item>
    ///   <item><term>6</term>  <description>UE import failed (non-zero exit, or python error marker on stdout)</description></item>
    ///   <item><term>9</term>  <description>NotImplemented (Pixel Streaming — Phase F)</description></item>
    ///   <item><term>64</term> <description>EX_USAGE (parse errors)</description></item>
    /// </list>
    /// </summary>
    internal static class ExitCodes
    {
        public const int Success = 0;
        public const int Failure = 1;
        /// <summary>UE 5.7 install couldn't be located (Phase E).</summary>
        public const int UeRootNotFound = 4;
        /// <summary>UE didn't emit a ready marker within the budget (Phase E).</summary>
        public const int UeTimeout = 5;
        /// <summary>UE import failed (non-zero exit, python error marker) (Phase E).</summary>
        public const int UeImportFailed = 6;
        /// <summary>Phase F path (Pixel Streaming) not implemented yet.</summary>
        public const int NotImplemented = 9;
        /// <summary>BSD EX_USAGE — bad arguments / missing flags.</summary>
        public const int Usage = 64;
    }

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand(
            "PRISM Visualiser orchestrator. Phase B scaffold — only --dry-run is functional.");

        root.AddCommand(BuildStreamCommand());
        root.AddCommand(BuildCacheCommand());
        return root;
    }

    // ------------------------------------------------------------
    // stream
    // ------------------------------------------------------------

    private static Command BuildStreamCommand()
    {
        var serverOption = new Option<string>(
            name: "--server",
            description: "ORBIT environment selector: prod | dev.")
        {
            IsRequired = true,
        }.FromAmong("prod", "dev");

        var projectOption = new Option<string>(
            name: "--project",
            description: "ORBIT project id.") { IsRequired = true };
        var modelOption = new Option<string>(
            name: "--model",
            description: "ORBIT model id.") { IsRequired = true };
        var versionOption = new Option<string>(
            name: "--version",
            description: "ORBIT version id (resolved object root).") { IsRequired = true };
        var runIdOption = new Option<string>(
            name: "--run-id",
            description: "Caller-supplied run UUID. Echoed in the ready event.")
            { IsRequired = true };
        var portHintOption = new Option<int>(
            name: "--signalling-port-hint",
            description: "Suggested local TCP port for Cirrus signalling. The orchestrator may pick a different one if the hint is in use.")
            { IsRequired = true };
        var jsonOption = new Option<bool>(
            name: "--json",
            description: "Required. Emits the ready event as a JSON line on stdout.");
        var dryRunOption = new Option<bool>(
            name: "--dry-run",
            description: "Skip fetch / import / spawn and emit a synthetic ready event.");

        var cmd = new Command("stream",
            "Launch a Pixel-Streaming session for an ORBIT model version.")
        {
            serverOption,
            projectOption,
            modelOption,
            versionOption,
            runIdOption,
            portHintOption,
            jsonOption,
            dryRunOption,
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var server = ctx.ParseResult.GetValueForOption(serverOption)!;
            var project = ctx.ParseResult.GetValueForOption(projectOption)!;
            var model = ctx.ParseResult.GetValueForOption(modelOption)!;
            var version = ctx.ParseResult.GetValueForOption(versionOption)!;
            var runId = ctx.ParseResult.GetValueForOption(runIdOption)!;
            var portHint = ctx.ParseResult.GetValueForOption(portHintOption);
            var json = ctx.ParseResult.GetValueForOption(jsonOption);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOption);

            if (!json)
            {
                Console.Error.WriteLine(
                    "[fatal] --json is required (the agent reads the ready event from stdout as JSON).");
                // ctx.ExitCode propagation through SetHandler is unreliable
                // in System.CommandLine 2.0-beta4 — Environment.Exit() is
                // the only way to guarantee the OS sees a non-zero code.
                ctx.ExitCode = ExitCodes.Usage;
                Environment.Exit(ExitCodes.Usage);
                return;
            }

            var (logger, logsDir) = StructuredLog.CreateRunLogger(runId);
            try
            {
                var manifest = new RunManifest(
                    RunId: runId,
                    ProjectId: project,
                    ModelId: model,
                    VersionId: version,
                    Server: ServerConfig.Resolve(server),
                    SignallingPortHint: portHint,
                    LogsDirectory: logsDir,
                    DryRun: dryRun);

                logger.Information(
                    "stream: server={Server} project={ProjectId} model={ModelId} version={VersionId} portHint={PortHint} dryRun={DryRun}",
                    manifest.Server.Name, manifest.ProjectId, manifest.ModelId,
                    manifest.VersionId, manifest.SignallingPortHint, manifest.DryRun);

                if (!dryRun)
                {
                    // NB: We intentionally do NOT call Environment.Exit here,
                    // even though some sibling paths in this handler do.
                    // RunPhaseEAsync's exit codes (4/5/6/9) must be propagated
                    // via ctx.ExitCode so the System.CommandLine parser
                    // returns them through Main. Calling Environment.Exit on
                    // this path deadlocked at process shutdown: the CLR's
                    // shutdown sequence races with the static
                    // JobObject/KillOnJobClose handle being finalised, and
                    // the async SetHandler state machine never unwinds.
                    var phaseEExit = await RunPhaseEAsync(
                            manifest, logger, ctx.GetCancellationToken())
                        .ConfigureAwait(false);
                    ctx.ExitCode = phaseEExit;
                    return;
                }

                // Cache root resolution is part of the dry run so any
                // pathing bug surfaces here instead of during a real
                // fetch.
                var cache = CacheRoot.ResolveDefault().EnsureCreated();
                logger.Information("cache={Cache}", cache);

                // Mimic some startup work so the agent observes the
                // expected "ready ~500ms after spawn" latency window.
                await Task.Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);

                // Synthesize plausible-but-clearly-fake values. The
                // 127.0.0.1:0 forms are intentional: they parse as
                // valid URIs but cannot collide with a real listener,
                // making them safe to surface in any agent-side
                // smoke test.
                var streamerId = $"orbit_{ShortId(runId)}";
                var ready = ReadyEvent.Ready(
                    runId: runId,
                    projectId: project,
                    modelId: model,
                    versionId: version,
                    playerUrl: "http://127.0.0.1:0/",
                    signallingUrl: "ws://127.0.0.1:0/",
                    streamerId: streamerId,
                    ueProcessId: 0,
                    signallingProcessId: 0,
                    logsDir: logsDir);

                ReadyHandshake.Write(ready);
                logger.Information("dry-run ready event emitted streamerId={StreamerId}",
                    streamerId);
                ctx.ExitCode = ExitCodes.Success;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "stream failed");
                try
                {
                    ReadyHandshake.Write(ReadyEvent.Failed(
                        runId, project, model, version, logsDir,
                        ex.Message));
                }
                catch
                {
                    // stdout may be closed — nothing more we can do.
                }
                FlushLogger(logger);
                ctx.ExitCode = ExitCodes.Failure;
                Environment.Exit(ExitCodes.Failure);
            }
            finally
            {
                FlushLogger(logger);
            }
        });

        return cmd;
    }

    // ------------------------------------------------------------
    // cache prune
    // ------------------------------------------------------------

    private static Command BuildCacheCommand()
    {
        var cache = new Command("cache",
            "Cache management. Phase B stubs `prune`; real eviction lands in Phase C.");

        var olderThanOption = new Option<string>(
            name: "--older-than",
            description: "Drop cache entries last accessed before <duration> ago. ISO 8601-ish (e.g. 14d, 12h, 30m).")
            { IsRequired = true };

        var prune = new Command("prune", "Evict cache entries older than the given duration.")
        {
            olderThanOption,
        };
        prune.SetHandler((string olderThan) =>
        {
            var (logger, _) = StructuredLog.CreateRunLogger($"cache-prune-{DateTime.UtcNow:yyyyMMddHHmmss}");
            try
            {
                var root = CacheRoot.ResolveDefault();
                logger.Information(
                    "cache prune (stub) olderThan={OlderThan} cache={Cache}",
                    olderThan, root);
                Console.Error.WriteLine(
                    $"[stub] cache prune olderThan={olderThan}; no entries evicted.");
            }
            finally
            {
                FlushLogger(logger);
            }
        }, olderThanOption);

        cache.AddCommand(prune);
        return cache;
    }

    private static string ShortId(string runId)
    {
        var trimmed = runId.Replace("-", string.Empty, StringComparison.Ordinal);
        return trimmed.Length >= 8 ? trimmed[..8] : trimmed;
    }

    /// <summary>
    /// Phase E end-to-end run: auth → receive → glTF stage → resolve UE
    /// install → fetch template → scaffold → launch UE editor → emit
    /// <c>imported/v1</c>. Returns the exit code the caller propagates.
    ///
    /// <para>
    /// stdout sequence on the happy path:
    /// <list type="number">
    ///   <item><description><c>prism-visualiser/staged/v1</c> (Phase C)</description></item>
    ///   <item><description><c>prism-visualiser/imported/v1</c> (Phase E)</description></item>
    /// </list>
    /// On failure, a <c>prism-visualiser/failed/v1</c> line is the last
    /// stdout event and the matching exit code is returned.
    /// </para>
    /// </summary>
    private static async Task<int> RunPhaseEAsync(
        RunManifest manifest, Serilog.ILogger logger, CancellationToken ct)
    {
        // 1. Resolve UE install BEFORE running the (slow) receive
        //    pipeline. If UNREAL_ENGINE_ROOT is set but invalid, we
        //    want to fail fast with a typed event — not after spending
        //    minutes downloading the version's blobs.
        var install = UnrealEnvironment.TryResolve();
        if (install is null)
        {
            var msg = UnrealEnvironment.EnvVarSet()
                ? $"{UnrealEnvironment.EnvVarName} is set but does not point at a valid UE 5.7 install."
                : $"UE 5.7 install not found via env var ({UnrealEnvironment.EnvVarName}), default path ({UnrealEnvironment.DefaultInstallRoot}), or registry ({UnrealEnvironment.RegistryKeyPath}).";
            logger.Error("ue env: {Message}", msg);
            EmitFailedEvent(manifest.RunId, FailedEvent.CodeUeRootNotFound, msg);
            return ExitCodes.UeRootNotFound;
        }
        logger.Information(
            "ue env: root={Root} editor={Editor} source={Source}",
            install.Root, install.EditorCmdPath, install.Source);

        // 2. Compose the pipeline. The Phase E pipeline owns the auth
        //    chain, the template fetcher, the scaffolder, and the
        //    JobObject the editor child process is added to.
        var tokenSource = CompositeOrbitTokenSource.Default();
        var fetcher = TemplateFetcher.CreateDefault(logger);
        var scaffolder = ProjectScaffolder.CreateDefault(logger);
        var pipeline = new VisualiserPipeline(
            tokenSource, fetcher, scaffolder, _processJob!, logger);

        // 3. Receive + stage. Emits the staged/v1 event so the agent
        //    has progress visibility even if UE later fails.
        StageOutcome stage;
        try
        {
            stage = await pipeline.ReceiveAndStageAsync(manifest, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "receive+stage failed");
            EmitFailedEvent(manifest.RunId, FailedEvent.CodeScaffoldFailed, ex.Message);
            return ExitCodes.Failure;
        }

        Console.Out.Write(stage.StagedEvent.ToJsonLine());
        Console.Out.Write('\n');
        Console.Out.Flush();
        logger.Information(
            "staged event emitted runId={RunId} stagePath={StagePath} meshCount={MeshCount} textureCount={TextureCount}",
            manifest.RunId, stage.StagedEvent.StagePath,
            stage.StagedEvent.MeshCount, stage.StagedEvent.TextureCount);

        // 4. Phase E continuation: template fetch + scaffold + UE
        //    launch. Each layer maps a typed exception to a typed
        //    failed/v1 event + a documented exit code.
        var templateTag = TemplateFetcher.DefaultTag;
        try
        {
            var imported = await pipeline
                .ImportAsync(manifest, install, templateTag, stage.GltfPath, ct)
                .ConfigureAwait(false);

            Console.Out.Write(imported.ImportedEvent.ToJsonLine());
            Console.Out.Write('\n');
            Console.Out.Flush();
            logger.Information(
                "imported event emitted runId={RunId} project={Project} level={Level} assets={Assets} importMs={ImportMs}",
                manifest.RunId,
                imported.ImportedEvent.ProjectPath,
                imported.ImportedEvent.LevelPath,
                imported.ImportedEvent.AssetCount,
                imported.ImportedEvent.ImportDurationMs);

            // Pixel Streaming bring-up is Phase F. End the run with
            // NotImplemented so the agent sees an explicit "we got
            // through Phase E but Phase F isn't here yet" signal.
            ReadyHandshake.Write(ReadyEvent.Failed(
                manifest.RunId, manifest.ProjectId, manifest.ModelId, manifest.VersionId,
                manifest.LogsDirectory,
                "Import complete; Pixel Streaming bring-up lands in Phase F."));
            return ExitCodes.NotImplemented;
        }
        catch (TemplateNotFoundException ex)
        {
            logger.Error(ex, "template not found tag={Tag}", templateTag);
            EmitFailedEvent(manifest.RunId, FailedEvent.CodeTemplateNotFound, ex.Message);
            return ExitCodes.Failure;
        }
        catch (TemplateFetchException ex)
        {
            logger.Error(ex, "template fetch failed tag={Tag}", templateTag);
            EmitFailedEvent(manifest.RunId, FailedEvent.CodeTemplateFetchFailed, ex.Message);
            return ExitCodes.Failure;
        }
        catch (UnrealLaunchTimeoutException ex)
        {
            logger.Error(ex, "ue import timed out");
            EmitFailedEvent(manifest.RunId, FailedEvent.CodeUeTimeout, ex.Message);
            return ExitCodes.UeTimeout;
        }
        catch (UnrealLaunchException ex)
        {
            logger.Error(ex, "ue import failed");
            EmitFailedEvent(manifest.RunId, FailedEvent.CodeUeImportFailed, ex.Message);
            return ExitCodes.UeImportFailed;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "phase E failed");
            EmitFailedEvent(manifest.RunId, FailedEvent.CodeScaffoldFailed, ex.Message);
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// Write a <c>prism-visualiser/failed/v1</c> JSON line to stdout.
    /// Best-effort: stdout-redirect failures fall through silently
    /// (the per-run log on disk has the full failure trace).
    /// </summary>
    private static void EmitFailedEvent(string runId, string code, string message)
    {
        try
        {
            var ev = FailedEvent.For(runId, code, message);
            Console.Out.Write(ev.ToJsonLine());
            Console.Out.Write('\n');
            Console.Out.Flush();
        }
        catch
        {
            // stdout closed, redirected to a dead pipe — nothing more
            // we can do. The on-disk log keeps the failure record.
        }
    }

    /// <summary>
    /// Flush + dispose the per-run Serilog logger. <see cref="Serilog.ILogger"/>
    /// itself does not expose <see cref="IDisposable"/>; the concrete
    /// <c>Logger</c> class returned by <see cref="StructuredLog.CreateRunLogger"/>
    /// does. Calling <see cref="IDisposable.Dispose"/> blocks until pending
    /// file writes flush, which is critical when we follow this with
    /// <see cref="Environment.Exit(int)"/>.
    /// </summary>
    private static void FlushLogger(Serilog.ILogger logger)
    {
        if (logger is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
