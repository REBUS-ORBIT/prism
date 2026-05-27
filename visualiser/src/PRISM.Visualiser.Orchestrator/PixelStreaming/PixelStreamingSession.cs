using System.Globalization;
using System.Runtime.Versioning;

using Serilog;

using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator.PixelStreaming;

/// <summary>
/// Composes the Phase F bring-up:
/// <list type="number">
///   <item><description>Allocate a free local TCP port for Cirrus.</description></item>
///   <item><description>Allocate a free local UDP range for WebRTC peer connections.</description></item>
///   <item><description>Spawn Cirrus + wait for its ready line.</description></item>
///   <item><description>Spawn UE in <c>-game</c> mode + wait for Cirrus to report the streamer connected.</description></item>
///   <item><description>Return a <see cref="PixelStreamingSession"/> ready to emit the final <c>prism-visualiser/ready/v1</c> event and block until shutdown.</description></item>
/// </list>
///
/// <para>
/// Cleanup ordering on cancellation is documented in
/// <see cref="ShutdownAsync"/>: UE first, then Cirrus. UE is the
/// thing actively encoding video and pushing it through Cirrus; if
/// Cirrus dies first, UE logs a flurry of WebRTC errors before
/// finally getting around to exiting. UE-first keeps the shutdown
/// log clean.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PixelStreamingSession : IAsyncDisposable
{
    /// <summary>Default wait budget for the streamer-connected line.</summary>
    public static readonly TimeSpan DefaultStreamerConnectTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Default grace period before force-killing on shutdown.</summary>
    public static readonly TimeSpan DefaultShutdownGrace = TimeSpan.FromSeconds(5);

    private readonly ILogger _log;
    private readonly UnrealGameHandle _ueHandle;
    private readonly SignallingHandle _cirrusHandle;
    private readonly TimeSpan _shutdownGrace;
    private bool _disposed;

    public PixelStreamingSession(
        ILogger log,
        UnrealGameHandle ueHandle,
        SignallingHandle cirrusHandle,
        TimeSpan? shutdownGrace = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ueHandle = ueHandle ?? throw new ArgumentNullException(nameof(ueHandle));
        _cirrusHandle = cirrusHandle ?? throw new ArgumentNullException(nameof(cirrusHandle));
        _shutdownGrace = shutdownGrace ?? DefaultShutdownGrace;
    }

    /// <summary>Cirrus signalling server's local TCP port.</summary>
    public int SignallingPort => _cirrusHandle.TcpPort;

    /// <summary>Streamer id UE was told to register as.</summary>
    public string StreamerId => _ueHandle.StreamerId;

    /// <summary>PID of the running UE -game process.</summary>
    public int UeProcessId => _ueHandle.ProcessId;

    /// <summary>PID of the running Cirrus signalling server.</summary>
    public int SignallingProcessId => _cirrusHandle.ProcessId;

    /// <summary>Loopback player URL the agent will publish to the server.</summary>
    public string PlayerUrl => string.Format(
        CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/", SignallingPort);

    /// <summary>Loopback signalling WS URL the agent will publish to the server.</summary>
    public string SignallingUrl => string.Format(
        CultureInfo.InvariantCulture, "ws://127.0.0.1:{0}/ws", SignallingPort);

    /// <summary>
    /// Build a <see cref="ReadyEvent"/> for this session. Composes the
    /// loopback URLs + PIDs the caller emits on stdout as the final
    /// <c>prism-visualiser/ready/v1</c> JSON line.
    /// </summary>
    public ReadyEvent BuildReadyEvent(
        string runId, string projectId, string modelId, string versionId, string logsDir) =>
        ReadyEvent.Ready(
            runId: runId,
            projectId: projectId,
            modelId: modelId,
            versionId: versionId,
            playerUrl: PlayerUrl,
            signallingUrl: SignallingUrl,
            streamerId: StreamerId,
            ueProcessId: UeProcessId,
            signallingProcessId: SignallingProcessId,
            logsDir: logsDir);

    /// <summary>
    /// Block until either UE exits or the cancellation token trips.
    /// Returns the UE exit code on natural exit, or -1 if the wait was
    /// cancelled. Cirrus is NOT awaited here — it only exits when we
    /// shut it down explicitly.
    /// </summary>
    public async Task<int> RunUntilExitAsync(CancellationToken ct)
    {
        try
        {
            await _ueHandle.WaitForExitAsync(ct).ConfigureAwait(false);
            var exit = _ueHandle.ExitCode;
            _log.Information("ue game exited code={ExitCode}", exit);
            return exit;
        }
        catch (OperationCanceledException)
        {
            _log.Information("session cancelled — initiating shutdown");
            return -1;
        }
    }

    /// <summary>
    /// Cancellation-safe shutdown of UE + Cirrus, in that order. Each
    /// gets <see cref="_shutdownGrace"/> to exit gracefully (we call
    /// <c>Kill</c> immediately on Windows because UE has no SIGTERM
    /// equivalent — the grace period is for the OS to reap the
    /// process); after the grace period the JobObject's
    /// KILL_ON_JOB_CLOSE backstop catches anything stuck.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await ShutdownAsync(
            ShutdownTargets.For(_ueHandle),
            ShutdownTargets.For(_cirrusHandle),
            _shutdownGrace, _log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Static cleanup primitive. Public so Test 15 can drive it
    /// against fake handles without spawning processes. Asserts the
    /// "UE first, then Cirrus" ordering by always awaiting each
    /// kill+wait pair before moving to the next.
    /// </summary>
    public static async Task ShutdownAsync(
        IShutdownTarget ueTarget,
        IShutdownTarget cirrusTarget,
        TimeSpan grace,
        ILogger log,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ueTarget);
        ArgumentNullException.ThrowIfNull(cirrusTarget);
        ArgumentNullException.ThrowIfNull(log);

        // 1. UE first. Kill, then wait up to `grace` for the process
        //    to actually exit. We don't propagate `ct` into the wait
        //    because the shutdown path itself must complete even when
        //    the parent cancellation already tripped.
        try
        {
            if (!ueTarget.HasExited)
            {
                log.Information("shutdown: killing UE pid={Pid}", ueTarget.ProcessId);
                ueTarget.Kill();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "shutdown: UE kill threw pid={Pid}", ueTarget.ProcessId);
        }
        await WaitForExitWithGraceAsync(ueTarget, grace, log, "ue").ConfigureAwait(false);

        // 2. Cirrus second.
        try
        {
            if (!cirrusTarget.HasExited)
            {
                log.Information("shutdown: killing Cirrus pid={Pid}", cirrusTarget.ProcessId);
                cirrusTarget.Kill();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "shutdown: Cirrus kill threw pid={Pid}", cirrusTarget.ProcessId);
        }
        await WaitForExitWithGraceAsync(cirrusTarget, grace, log, "cirrus").ConfigureAwait(false);
    }

    private static async Task WaitForExitWithGraceAsync(
        IShutdownTarget target, TimeSpan grace, ILogger log, string label)
    {
        try
        {
            using var cts = new CancellationTokenSource(grace);
            await target.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            log.Information("shutdown: {Label} pid={Pid} exited within grace", label, target.ProcessId);
        }
        catch (OperationCanceledException)
        {
            log.Warning(
                "shutdown: {Label} pid={Pid} did not exit within {GraceMs}ms — JobObject backstop will reclaim it",
                label, target.ProcessId, (int)grace.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            log.Warning(ex,
                "shutdown: {Label} pid={Pid} wait threw",
                label, target.ProcessId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        finally
        {
            await _ueHandle.DisposeAsync().ConfigureAwait(false);
            await _cirrusHandle.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Abstraction over a killable child process. Tests use a fake
/// implementation to drive
/// <see cref="PixelStreamingSession.ShutdownAsync(IShutdownTarget, IShutdownTarget, TimeSpan, ILogger, CancellationToken)"/>
/// without spawning real processes; production wires
/// <see cref="UnrealGameHandle"/> + <see cref="SignallingHandle"/>
/// through the adapters in <see cref="ShutdownTargets"/>.
/// </summary>
public interface IShutdownTarget
{
    int ProcessId { get; }
    bool HasExited { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken ct);
}

/// <summary>
/// Adapters from concrete process handles to <see cref="IShutdownTarget"/>.
/// Kept as a thin static helper so the production code path stays
/// allocation-free at the type level.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShutdownTargets
{
    public static IShutdownTarget For(UnrealGameHandle handle) =>
        new UnrealGameTarget(handle);

    public static IShutdownTarget For(SignallingHandle handle) =>
        new SignallingTarget(handle);

    private sealed class UnrealGameTarget : IShutdownTarget
    {
        private readonly UnrealGameHandle _h;
        public UnrealGameTarget(UnrealGameHandle h) { _h = h; }
        public int ProcessId => _h.ProcessId;
        public bool HasExited => _h.HasExited;
        public void Kill() => _h.Kill();
        public Task WaitForExitAsync(CancellationToken ct) => _h.WaitForExitAsync(ct);
    }

    private sealed class SignallingTarget : IShutdownTarget
    {
        private readonly SignallingHandle _h;
        public SignallingTarget(SignallingHandle h) { _h = h; }
        public int ProcessId => _h.ProcessId;
        public bool HasExited => _h.HasExited;
        public void Kill() => _h.Kill();
        public Task WaitForExitAsync(CancellationToken ct) => _h.WaitForExitAsync(ct);
    }
}
