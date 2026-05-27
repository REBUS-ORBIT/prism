using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PRISM.Agent.Ws;
using PRISM.Contracts;

namespace PRISM.Agent.Visualiser;

/// <summary>
/// Owns the lifecycle of every active <see cref="SignallingBridge"/> on
/// this agent, keyed by <c>runId</c>. One singleton per agent process.
///
/// Two ways a bridge is born:
///   1. <see cref="RegisterLocalCirrus"/> — eager. The Phase E/F
///      orchestrator (when it lands) will call this from its
///      <c>ready/v1</c> emit so the bridge is already connected by the
///      time the first signalling frame arrives from the server.
///   2. <see cref="GetOrCreateAsync"/> — lazy. Until the orchestrator is
///      integrated on this branch (Phase G stubbed the inbound handler;
///      Phase I wires the bridge) the first inbound frame implies the
///      orchestrator has booted with a local Cirrus listening on the
///      configured default port. We try to connect; if there's no
///      listener yet the call surfaces the failure and the upstream
///      handler can ack-reject.
///
/// Disposal:
///   - <see cref="DropAsync"/> — call on visualisationEnded /
///     visualisationFailed to tear down a single bridge.
///   - <see cref="DisposeAsync"/> — agent shutdown.
/// </summary>
public sealed class SignallingBridgeRegistry : IAsyncDisposable
{
    readonly ConcurrentDictionary<string, SignallingBridge> _bridges = new();
    readonly ConcurrentDictionary<string, Uri> _knownLocalUrls = new();
    readonly ILoggerFactory _loggerFactory;
    readonly ILogger<SignallingBridgeRegistry> _log;
    readonly WsClient _ws;

    /// <summary>Default Cirrus port for the lazy-instantiation fallback.
    /// Overridable via <c>PRISM_VISUALISER_CIRRUS_URL</c> env var so
    /// dev / CI can point at a different signaller. The Pixel
    /// Streaming reference Cirrus binds <c>:8888</c> by default.</summary>
    public static Uri DefaultLocalCirrusUrl =>
        new(Environment.GetEnvironmentVariable("PRISM_VISUALISER_CIRRUS_URL") ?? "ws://127.0.0.1:8888/");

    public SignallingBridgeRegistry(WsClient ws, ILoggerFactory loggerFactory)
    {
        _ws            = ws;
        _loggerFactory = loggerFactory;
        _log           = loggerFactory.CreateLogger<SignallingBridgeRegistry>();
    }

    /// <summary>
    /// Tell the registry which local Cirrus URL belongs to <paramref name="runId"/>.
    /// The orchestrator's <c>ready/v1</c> handler is the natural caller;
    /// invoking it before <see cref="GetOrCreateAsync"/> ensures the
    /// first inbound signalling frame routes to the right port.
    /// </summary>
    public void RegisterLocalCirrus(string runId, Uri localCirrusUrl)
    {
        if (string.IsNullOrEmpty(runId)) throw new ArgumentException("runId is required", nameof(runId));
        ArgumentNullException.ThrowIfNull(localCirrusUrl);
        _knownLocalUrls[runId] = localCirrusUrl;
        _log.LogInformation("signalling bridge registry: registered local Cirrus URL {Url} for runId={RunId}", localCirrusUrl, runId);
    }

    /// <summary>
    /// Get the existing bridge for <paramref name="runId"/> or create &
    /// connect a new one against the registered (or default) local
    /// Cirrus URL. Connection failures are logged + propagated so the
    /// caller can ack-reject the inbound envelope.
    /// </summary>
    public async Task<SignallingBridge> GetOrCreateAsync(string runId, CancellationToken ct = default)
    {
        if (_bridges.TryGetValue(runId, out var existing) && existing.IsOpen)
            return existing;

        if (!_knownLocalUrls.TryGetValue(runId, out var url))
            url = DefaultLocalCirrusUrl;

        // Race protection: GetOrAdd can run the factory more than once
        // under contention; we keep the first winner and Dispose any
        // losers. The factory must be sync, so the WS connect happens
        // outside and we then atomically swap the placeholder out.
        var bridge = new SignallingBridge(
            runId,
            url,
            SendUpstreamAsync,
            _loggerFactory.CreateLogger<SignallingBridge>());

        var added = _bridges.GetOrAdd(runId, bridge);
        if (!ReferenceEquals(added, bridge))
        {
            // Another concurrent caller already inserted a bridge; ditch ours.
            bridge.Dispose();
            return added;
        }

        try
        {
            await bridge.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Failed to connect — remove the registration so the next
            // attempt re-tries instead of returning a dead bridge.
            _bridges.TryRemove(KeyValuePair.Create(runId, bridge));
            bridge.Dispose();
            throw;
        }
        return bridge;
    }

    /// <summary>
    /// Look up an existing bridge without creating one. Returns null
    /// if no bridge has been registered/created for <paramref name="runId"/>.
    /// </summary>
    public SignallingBridge? TryGet(string runId)
        => _bridges.TryGetValue(runId, out var bridge) ? bridge : null;

    /// <summary>
    /// Tear down the bridge for a single runId. Safe to call multiple
    /// times — second call is a no-op.
    /// </summary>
    public Task DropAsync(string runId)
    {
        _knownLocalUrls.TryRemove(runId, out _);
        if (_bridges.TryRemove(runId, out var bridge))
        {
            bridge.Dispose();
            _log.LogInformation("signalling bridge registry: dropped runId={RunId}", runId);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cirrus → server upstream sender. Wraps the frame into a
    /// <c>signallingFrame</c> envelope and pushes it onto the agent
    /// WS outbox.
    /// </summary>
    async ValueTask SendUpstreamAsync(string runId, ReadOnlyMemory<byte>? binary, string? text)
    {
        var data = new SignallingFrameData { RunId = runId };
        if (binary is { } b && b.Length > 0)
        {
            data.PayloadB64 = Convert.ToBase64String(b.Span);
        }
        else if (!string.IsNullOrEmpty(text))
        {
            data.Payload = text;
        }
        else
        {
            return; // nothing to send
        }
        try
        {
            await _ws.SendAsync(MessageType.SignallingFrame, data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "signalling bridge registry: failed to push frame upstream for runId={RunId}", runId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, bridge) in _bridges)
        {
            bridge.Dispose();
        }
        _bridges.Clear();
        _knownLocalUrls.Clear();
        await Task.CompletedTask;
    }
}
