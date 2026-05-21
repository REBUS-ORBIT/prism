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

    public string? SessionId { get; private set; }

    public AgentMessageDispatcher(WsClient ws, WorkerSlotPool pool, ILogger<AgentMessageDispatcher> log)
    {
        _ws = ws;
        _pool = pool;
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
