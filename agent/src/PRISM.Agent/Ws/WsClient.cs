using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PRISM.Contracts;
using Websocket.Client;

namespace PRISM.Agent.Ws;

/// <summary>
/// Auto-reconnecting WS client for the PRISM agent.
///
/// Handles framing, reconnect back-off, and an outbound channel so the
/// agent never blocks on socket writes. Incoming frames are surfaced via
/// the <see cref="OnMessage"/> event after JSON deserialisation.
/// </summary>
public sealed class WsClient : IAsyncDisposable
{
    readonly ILogger<WsClient> _log;
    readonly WebsocketClient _ws;
    readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    int _connectedFlag; // 1 = connected, 0 = not

    public event Action<MessageType, string>? OnMessage;
    public event Action? OnReconnected;
    public event Action? OnDisconnected;

    /// <summary>True while the websocket has an active connection.</summary>
    public bool IsConnected => Volatile.Read(ref _connectedFlag) == 1;

    public WsClient(Uri url, ILogger<WsClient> log)
    {
        _log = log;
        var factory = new Func<ClientWebSocket>(() =>
        {
            var client = new ClientWebSocket();
            client.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            return client;
        });
        _ws = new WebsocketClient(url, factory)
        {
            ReconnectTimeout    = TimeSpan.FromSeconds(60),
            ErrorReconnectTimeout = TimeSpan.FromSeconds(15),
            IsReconnectionEnabled = true,
        };

        _ws.MessageReceived.Subscribe(OnSocketMessage);
        _ws.ReconnectionHappened.Subscribe(info =>
        {
            Volatile.Write(ref _connectedFlag, 1);
            _log.LogInformation("ws reconnected: {Type}", info.Type);
            OnReconnected?.Invoke();
        });
        _ws.DisconnectionHappened.Subscribe(info =>
        {
            Volatile.Write(ref _connectedFlag, 0);
            _log.LogWarning("ws disconnected: {Type} {Description}", info.Type, info.CloseStatusDescription);
            OnDisconnected?.Invoke();
        });
    }

    public Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() => PumpOutboxAsync(ct), ct);
        return _ws.Start();
    }

    public ValueTask SendAsync<TData>(MessageType type, TData data, string? id = null)
    {
        var env  = Envelope<TData>.New(type, data, id);
        var json = JsonConvert.SerializeObject(env);
        return _outbox.Writer.WriteAsync(json);
    }

    /// <summary>
    /// Disconnect cleanly and disable auto-reconnect (tray "Stop Agent").
    /// </summary>
    public async Task PauseAsync()
    {
        _ws.IsReconnectionEnabled = false;
        await _ws.Stop(WebSocketCloseStatus.NormalClosure, "agent paused");
        Volatile.Write(ref _connectedFlag, 0);
    }

    /// <summary>
    /// Re-enable auto-reconnect and trigger an immediate reconnect attempt (tray "Start Agent").
    /// </summary>
    public void Resume()
    {
        _ws.IsReconnectionEnabled = true;
        _ws.Reconnect();
    }

    async Task PumpOutboxAsync(CancellationToken ct)
    {
        await foreach (var frame in _outbox.Reader.ReadAllAsync(ct))
        {
            try
            {
                _ws.Send(frame);
            }
            catch (Exception err)
            {
                _log.LogWarning(err, "ws send failed; will retry on reconnect");
            }
        }
    }

    void OnSocketMessage(ResponseMessage msg)
    {
        if (msg.MessageType != WebSocketMessageType.Text || string.IsNullOrEmpty(msg.Text))
            return;

        // Peek at the type field to dispatch. Keep the original JSON so the
        // caller can re-parse into the right concrete type.
        try
        {
            var probe = JsonConvert.DeserializeObject<EnvelopeProbe>(msg.Text);
            if (probe is null) return;
            OnMessage?.Invoke(probe.Type, msg.Text);
        }
        catch (Exception err)
        {
            _log.LogWarning(err, "failed to parse ws frame: {Text}", msg.Text);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        await _ws.Stop(WebSocketCloseStatus.NormalClosure, "agent shutdown");
        _ws.Dispose();
    }

    sealed class EnvelopeProbe
    {
        [JsonProperty("type")] public MessageType Type { get; set; }
    }
}
