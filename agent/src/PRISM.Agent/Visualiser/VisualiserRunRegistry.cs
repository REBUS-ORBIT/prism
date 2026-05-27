using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PRISM.Agent.Pipeline;

namespace PRISM.Agent.Visualiser;

/// <summary>
/// Owns the set of <see cref="VisualiserJob"/> instances currently
/// driving an orchestrator process on this workstation. Keyed by the
/// server-supplied <c>runId</c>.
///
/// <para>
/// Two consumers:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="Ws.AgentMessageDispatcher"/> calls <see cref="TryStart"/>
///     on <c>startVisualisation</c> and <see cref="TryCancel"/> on
///     <c>cancelVisualisation</c>.
///   </description></item>
///   <item><description>
///     The job itself calls <see cref="Remove"/> from its terminal
///     bookkeeping so a subsequent <c>startVisualisation</c> for the
///     same runId (rare — clients reuse runIds across reconnects) is
///     not rejected as already-active.
///   </description></item>
/// </list>
///
/// <para>
/// Concurrency is bounded by <see cref="MaxConcurrent"/>. The PRISM
/// server independently enforces the per-workstation slot cap before
/// dispatching, but a defensive in-agent gate keeps a misconfigured
/// server from accidentally double-booking a single-GPU box.
/// </para>
/// </summary>
public sealed class VisualiserRunRegistry : IAsyncDisposable
{
    readonly ILogger<VisualiserRunRegistry> _log;
    readonly ConcurrentDictionary<string, VisualiserJob> _runs = new();

    /// <summary>Hard cap on concurrent orchestrator processes.</summary>
    public int MaxConcurrent { get; }

    /// <summary>Number of currently-running orchestrator processes.</summary>
    public int ActiveCount => _runs.Count;

    public VisualiserRunRegistry(ILogger<VisualiserRunRegistry> log, int maxConcurrent)
    {
        _log = log;
        MaxConcurrent = Math.Max(1, maxConcurrent);
    }

    /// <summary>
    /// Atomically reserve a slot for <paramref name="runId"/>. Returns
    /// the registered job on success; on contention (already running,
    /// or cap reached) returns <c>null</c> and the caller should
    /// ack-reject the inbound envelope.
    /// </summary>
    public VisualiserJob? TryStart(string runId, Func<VisualiserJob> jobFactory)
    {
        if (_runs.ContainsKey(runId))
        {
            _log.LogWarning("visualiser registry: refusing duplicate runId={RunId} (already active)", runId);
            return null;
        }
        if (_runs.Count >= MaxConcurrent)
        {
            _log.LogWarning(
                "visualiser registry: refusing runId={RunId} — active={Active} >= max={Max}",
                runId, _runs.Count, MaxConcurrent);
            return null;
        }
        var job = jobFactory();
        if (!_runs.TryAdd(runId, job))
        {
            _log.LogWarning("visualiser registry: lost race adding runId={RunId}", runId);
            return null;
        }
        return job;
    }

    /// <summary>
    /// Remove <paramref name="runId"/> from the active set without
    /// touching the underlying job. Idempotent.
    /// </summary>
    public void Remove(string runId)
    {
        if (_runs.TryRemove(runId, out _))
        {
            _log.LogInformation("visualiser registry: removed runId={RunId}", runId);
        }
    }

    /// <summary>
    /// Look up the live job for <paramref name="runId"/>, or
    /// <c>null</c> if no run is active.
    /// </summary>
    public VisualiserJob? TryGet(string runId)
        => _runs.TryGetValue(runId, out var job) ? job : null;

    /// <summary>
    /// Cancel the orchestrator for <paramref name="runId"/> if it's
    /// running. Returns true if a job was found (and cancellation was
    /// requested), false if the runId is unknown.
    /// </summary>
    public bool TryCancel(string runId, string? reason)
    {
        if (_runs.TryGetValue(runId, out var job))
        {
            job.RequestCancel(reason);
            return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (runId, job) in _runs)
        {
            try
            {
                job.RequestCancel("agent shutdown");
                await job.WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "visualiser registry: dispose failed for runId={RunId}", runId);
            }
        }
        _runs.Clear();
    }
}
