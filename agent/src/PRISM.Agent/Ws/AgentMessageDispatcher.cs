using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PRISM.Agent.Pipeline;
using PRISM.Agent.Visualiser;
using PRISM.Contracts;

namespace PRISM.Agent.Ws;

/// <summary>
/// Routes incoming server-&gt;agent messages.
/// Phase 3: real `Assign` hand-off to <see cref="WorkerSlotPool"/>.
/// </summary>
public sealed class AgentMessageDispatcher
{
    readonly ILogger<AgentMessageDispatcher> _log;
    readonly WsClient _ws;
    readonly WorkerSlotPool _pool;
    readonly IServiceProvider _sp;

    public string? SessionId { get; private set; }

    public AgentMessageDispatcher(WsClient ws, WorkerSlotPool pool, IServiceProvider sp, ILogger<AgentMessageDispatcher> log)
    {
        _ws = ws;
        _pool = pool;
        _sp = sp;
        _log = log;
        _ws.OnMessage += Handle;
    }

    void Handle(MessageType type, string rawJson)
    {
        try
        {
            switch (type)
            {
                case MessageType.Welcome:    HandleWelcome(rawJson);    return;
                case MessageType.ServerPing: return; // keepalive from server; no response needed
                case MessageType.Assign:     HandleAssign(rawJson);     return;
                case MessageType.Cancel:     HandleCancel(rawJson);     return;
                case MessageType.PollLayers: HandlePollLayers(rawJson); return;
                case MessageType.Restart:    HandleRestart(rawJson);    return;
                case MessageType.Update:     HandleUpdate(rawJson);     return;
                // Visualiser — spawns the sidecar
                // PRISM.Visualiser.Orchestrator.exe, pumps its stdout
                // JSON events back upstream, and forwards cancel
                // requests by killing the orchestrator process tree.
                case MessageType.StartVisualisation:  HandleStartVisualisation(rawJson);  return;
                case MessageType.CancelVisualisation: HandleCancelVisualisation(rawJson); return;
                case MessageType.SignallingFrame:     HandleSignallingFrame(rawJson);     return;
                default:
                    _log.LogDebug("dispatcher ignoring inbound type {Type}", type);
                    return;
            }
        }
        catch (Exception err)
        {
            _log.LogError(err, "dispatcher failed handling {Type}", type);
        }
    }

    void HandleWelcome(string raw)
    {
        var env = ParseEnvelope<WelcomeData>(raw);
        SessionId = env?.Data?.SessionId;
        _log.LogInformation("welcome: sessionId={Sid} heartbeatSec={Hb}", SessionId, env?.Data?.HeartbeatSeconds);
    }

    void HandleAssign(string raw)
    {
        var env = ParseEnvelope<AssignData>(raw);
        if (env?.Data is null) return;
        _log.LogInformation("assign: jobId={JobId} slot={Slot} format={Format} file={FileName}",
            env.Data.JobId, env.Data.Slot, env.Data.Format, env.Data.FileName ?? "");

        _ = _ws.SendAsync(MessageType.Ack, new AckData { JobId = env.Data.JobId, Accepted = true });
        _pool.Enqueue(env.Data);
    }

    void HandleCancel(string raw)
    {
        var env = ParseEnvelope<CancelData>(raw);
        if (env?.Data is null) return;
        _log.LogInformation("cancel: jobId={JobId} reason={Reason}", env.Data.JobId, env.Data.Reason ?? "");
    }

    void HandlePollLayers(string raw)
    {
        var env = ParseEnvelope<PollLayersData>(raw);
        if (env?.Data is null) return;
        _log.LogInformation("pollLayers: jobId={JobId} file={FileUrl} format={Format}",
            env.Data.JobId, env.Data.FileUrl, env.Data.Format);
        // Ack first so the server stops treating us as unresponsive on this
        // jobId, then drop into the slot pool so the layer extraction runs
        // serialised against any in-flight convert (Rhino is not re-entrant).
        _ = _ws.SendAsync(MessageType.Ack, new AckData { JobId = env.Data.JobId, Accepted = true });
        _pool.EnqueuePollLayers(env.Data);
    }

    void HandleRestart(string raw)
    {
        var env = ParseEnvelope<RestartData>(raw);
        var reason = env?.Data?.Reason;
        _log.LogWarning("restart requested by server (reason={Reason})", reason ?? "<none>");
        // Pulled lazily to keep a one-way dependency: dispatcher ->
        // control plane, never the reverse.
        var plane = _sp.GetRequiredService<AgentControlPlane>();
        _ = plane.RestartAsync(reason);
    }

    void HandleUpdate(string raw)
    {
        var env = ParseEnvelope<UpdateData>(raw);
        var tag = env?.Data?.Tag;
        _log.LogInformation("update requested by server (tag={Tag})", tag ?? "<latest>");
        var plane = _sp.GetRequiredService<AgentControlPlane>();
        // Fire-and-forget on the WS pump thread, but inspect the outcome
        // so v0.1.36's "already-running" short-circuit surfaces as a
        // single WARN log line on the agent (and therefore on the
        // server's log pipeline) rather than a silent no-op.
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await plane.CheckAndApplyUpdateAsync(tag);
                if (outcome.AlreadyRunning)
                {
                    _log.LogWarning(
                        "remote update request ignored — another update is already in progress on this agent (tag={Tag})",
                        tag ?? "<latest>");
                }
                else if (outcome.Error is { } err && !outcome.UpdateAvailable)
                {
                    _log.LogError(
                        "remote update request failed (tag={Tag}): {Error}",
                        tag ?? "<latest>", err);
                }
            }
            catch (InvalidOperationException ex)
            {
                // Belt-and-braces: if a race slipped past the
                // IsUpdateInProgress short-circuit inside the control
                // plane and the gate rejected us anyway, log as WARN
                // rather than ERROR so the admin UI doesn't flag a
                // benign collision as a real failure.
                _log.LogWarning(ex,
                    "remote update collided with an in-flight update on this agent");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "remote update handler threw");
            }
        });
    }

    void HandleStartVisualisation(string raw)
    {
        var env = ParseEnvelope<StartVisualisationData>(raw);
        if (env?.Data is null) return;
        var data = env.Data;

        var registry = _sp.GetRequiredService<VisualiserRunRegistry>();
        var job = registry.TryStart(data.RunId, () => _sp.GetRequiredService<VisualiserJob>());
        if (job is null)
        {
            _log.LogWarning(
                "startVisualisation refused for runId={RunId}: active={Active}/{Max} (duplicate runId or cap reached)",
                data.RunId, registry.ActiveCount, registry.MaxConcurrent);
            _ = _ws.SendAsync(MessageType.Ack, new AckData
            {
                JobId    = data.RunId,
                Accepted = false,
                Reason   = "visualiser slot unavailable",
            });
            return;
        }

        _log.LogInformation(
            "startVisualisation accepted for runId={RunId} project={ProjectId} model={ModelId} version={VersionId} — spawning orchestrator",
            data.RunId, data.ProjectId, data.ModelId, data.VersionId ?? string.Empty);

        _ = _ws.SendAsync(MessageType.Ack, new AckData
        {
            JobId    = data.RunId,
            Accepted = true,
        });

        // StartAsync returns once the process is alive (or has failed
        // to launch). The actual streaming work continues on
        // background tasks owned by the job; the dispatcher thread
        // must not block here or every other inbound envelope
        // (heartbeat, signallingFrame) would pile up behind a slow
        // UE bring-up.
        _ = Task.Run(async () =>
        {
            try
            {
                await job.StartAsync(data, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "startVisualisation: job.StartAsync threw for runId={RunId}",
                    data.RunId);
            }
        });
    }

    void HandleCancelVisualisation(string raw)
    {
        var env = ParseEnvelope<CancelVisualisationData>(raw);
        if (env?.Data is null) return;
        var registry = _sp.GetRequiredService<VisualiserRunRegistry>();
        var reason = env.Data.Reason;
        if (registry.TryCancel(env.Data.RunId, reason))
        {
            _log.LogInformation(
                "cancelVisualisation: cancelling runId={RunId} reason={Reason}",
                env.Data.RunId, reason ?? "<none>");
            _ = _ws.SendAsync(MessageType.Ack, new AckData
            {
                JobId    = env.Data.RunId,
                Accepted = true,
            });
        }
        else
        {
            _log.LogWarning(
                "cancelVisualisation: unknown runId={RunId} (already exited or never started); acking",
                env.Data.RunId);
            _ = _ws.SendAsync(MessageType.Ack, new AckData
            {
                JobId    = env.Data.RunId,
                Accepted = true,
                Reason   = "no active run for this runId",
            });
        }
    }

    /// <summary>
    /// Forward a Pixel Streaming signalling frame to the local Cirrus WS
    /// owned by the visualiser orchestrator for <c>runId</c>.
    ///
    /// Phase I: real bridge wired via <see cref="SignallingBridgeRegistry"/>.
    /// The bridge is lazy-created on first inbound frame (falling back to
    /// <see cref="SignallingBridgeRegistry.DefaultLocalCirrusUrl"/>) so we
    /// stay forward-compatible with the upcoming orchestrator branch,
    /// which will publish its local Cirrus URL via
    /// <c>RegisterLocalCirrus</c> before any frames flow.
    /// </summary>
    void HandleSignallingFrame(string raw)
    {
        var env = ParseEnvelope<SignallingFrameData>(raw);
        if (env?.Data is null || string.IsNullOrEmpty(env.Data.RunId))
        {
            _log.LogWarning("signallingFrame ignored: missing data/runId");
            return;
        }

        // Decode binary up front so we don't hold the dispatcher thread
        // on a base64 conversion. Per the contract exactly one of the
        // two payload fields is set; if both are absent we still forward
        // an empty frame so the bridge can log/drop.
        byte[]? binary = !string.IsNullOrEmpty(env.Data.PayloadB64)
            ? Convert.FromBase64String(env.Data.PayloadB64)
            : null;
        var text = env.Data.Payload;

        // Fire-and-forget — the same pattern HandleUpdate uses. The WS
        // pump thread must not block on a local-Cirrus connect that may
        // take O(seconds) on first frame.
        var registry = _sp.GetRequiredService<SignallingBridgeRegistry>();
        var runId = env.Data.RunId;
        _ = Task.Run(async () =>
        {
            try
            {
                var bridge = await registry.GetOrCreateAsync(runId).ConfigureAwait(false);
                await bridge.ForwardToLocalAsync(text, binary, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "signallingFrame: failed to forward to local Cirrus for runId={RunId} (textLen={TextLen} binLen={BinLen})",
                    runId, text?.Length ?? 0, binary?.Length ?? 0);
            }
        });
    }

    static Envelope<T>? ParseEnvelope<T>(string raw)
    {
        // Two-step: parse into JObject, then re-bind data into the typed T.
        var obj = JObject.Parse(raw);
        var env = new Envelope<T>
        {
            Version   = obj.Value<int>("v"),
            Type      = obj["type"]!.ToObject<MessageType>(),
            Id        = obj.Value<string?>("id"),
            Timestamp = obj.Value<string?>("ts"),
            Data      = obj["data"]!.ToObject<T>()!,
        };
        return env;
    }
}
