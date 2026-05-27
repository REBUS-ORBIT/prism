using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PRISM.Agent.Pipeline;
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
                // Visualiser (Phase A scaffold — orchestrator binary lands in Phase F/G).
                // Both handlers currently log a WARN and ack `accepted: false`.
                case MessageType.StartVisualisation:  HandleStartVisualisation(rawJson);  return;
                case MessageType.CancelVisualisation: HandleCancelVisualisation(rawJson); return;
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
        _log.LogWarning(
            "startVisualisation received for runId={RunId} project={ProjectId} model={ModelId} — visualiser orchestrator not yet implemented; acking accepted=false (Phase A scaffold)",
            env.Data.RunId, env.Data.ProjectId, env.Data.ModelId);
        // Reuse AckData so the server stops treating the runId as
        // unresponsive. Phase G/F will replace this with a real
        // VisualiserSession handoff and the reverse-channel
        // visualisationReady / visualisationFailed envelopes.
        _ = _ws.SendAsync(MessageType.Ack, new AckData
        {
            JobId    = env.Data.RunId,
            Accepted = false,
            Reason   = "visualiser orchestrator not yet implemented",
        });
    }

    void HandleCancelVisualisation(string raw)
    {
        var env = ParseEnvelope<CancelVisualisationData>(raw);
        if (env?.Data is null) return;
        _log.LogWarning(
            "cancelVisualisation received for runId={RunId} reason={Reason} — visualiser orchestrator not yet implemented; acking accepted=false (Phase A scaffold)",
            env.Data.RunId, env.Data.Reason ?? "<none>");
        _ = _ws.SendAsync(MessageType.Ack, new AckData
        {
            JobId    = env.Data.RunId,
            Accepted = false,
            Reason   = "visualiser orchestrator not yet implemented",
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
