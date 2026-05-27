using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using PRISM.Agent.Visualiser;
using Xunit;

namespace PRISM.Agent.Tests;

/// <summary>
/// Exercises the <see cref="SignallingBridge"/> against an in-process
/// WebSocket echo server bound to a randomly-picked local TCP port.
///
/// The bridge is the only piece of the Phase I agent surface that
/// touches sockets, so it carries the bulk of the unit-test burden.
/// The registry's logic on top is a few-line <see cref="Dictionary{K,V}"/>
/// wrapper and is exercised transitively through the bridge tests.
/// </summary>
public sealed class SignallingBridgeTests
{
    /// <summary>
    /// Server-to-agent text frame should be reassembled and written to
    /// the local Cirrus socket verbatim; the reverse-channel pump should
    /// surface every echoed frame as an upstream callback.
    /// </summary>
    [Fact]
    public async Task ForwardsTextFrames_RoundTripsThroughEchoServer()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        var received = new List<string>();
        var binaryReceived = new List<byte[]>();

        await using var bridge = new BridgeUnderTest(server.Url, received, binaryReceived);
        await bridge.Inner.StartAsync(CancellationToken.None);
        Assert.True(bridge.Inner.IsOpen, "bridge should be open after StartAsync");

        const string payload = "{\"type\":\"offer\",\"sdp\":\"v=0\\r\\n\"}";
        await bridge.Inner.ForwardToLocalAsync(payload, binary: null, CancellationToken.None);

        // Drain the echo's response. The echo bounces each frame
        // straight back, so we wait for the upstream callback to fire.
        await WaitForAsync(() => received.Count >= 1, TimeSpan.FromSeconds(2));
        Assert.Single(received);
        Assert.Equal(payload, received[0]);
        Assert.Empty(binaryReceived);
    }

    [Fact]
    public async Task ForwardsBinaryFrames_RoundTripsThroughEchoServer()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        var textReceived = new List<string>();
        var binaryReceived = new List<byte[]>();

        await using var bridge = new BridgeUnderTest(server.Url, textReceived, binaryReceived);
        await bridge.Inner.StartAsync(CancellationToken.None);

        var bytes = new byte[] { 0x01, 0x02, 0xFE, 0xFF, 0x00, 0x10, 0x20, 0x30 };
        await bridge.Inner.ForwardToLocalAsync(text: null, binary: bytes, CancellationToken.None);

        await WaitForAsync(() => binaryReceived.Count >= 1, TimeSpan.FromSeconds(2));
        Assert.Single(binaryReceived);
        Assert.Equal(bytes, binaryReceived[0]);
        Assert.Empty(textReceived);
    }

    /// <summary>
    /// Multiple frames in quick succession should be preserved in order
    /// and each surface as exactly one upstream callback.
    /// </summary>
    [Fact]
    public async Task ForwardsMultipleFrames_PreservesOrder()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        var received = new List<string>();
        var binaryReceived = new List<byte[]>();

        await using var bridge = new BridgeUnderTest(server.Url, received, binaryReceived);
        await bridge.Inner.StartAsync(CancellationToken.None);

        var frames = new[] { "first", "second", "third" };
        foreach (var frame in frames)
            await bridge.Inner.ForwardToLocalAsync(frame, binary: null, CancellationToken.None);

        await WaitForAsync(() => received.Count >= frames.Length, TimeSpan.FromSeconds(2));
        Assert.Equal(frames, received);
    }

    /// <summary>
    /// Dispose should tear the local WS down and stop the pump so the
    /// bridge can be safely disposed even when the server is still
    /// echoing. No upstream callbacks should fire after disposal.
    /// </summary>
    [Fact]
    public async Task Dispose_TearsDownPumpAndStopsCallbacks()
    {
        await using var server = await EchoWebSocketServer.StartAsync();
        var received = new List<string>();
        var binaryReceived = new List<byte[]>();

        var bridge = new BridgeUnderTest(server.Url, received, binaryReceived);
        await bridge.Inner.StartAsync(CancellationToken.None);
        await bridge.Inner.ForwardToLocalAsync("before-dispose", binary: null, CancellationToken.None);
        await WaitForAsync(() => received.Count >= 1, TimeSpan.FromSeconds(2));

        bridge.Inner.Dispose();
        var snapshot = received.Count;

        // Give any in-flight callbacks a beat to land — none should.
        await Task.Delay(200);
        Assert.Equal(snapshot, received.Count);
        Assert.False(bridge.Inner.IsOpen);
    }

    /// <summary>
    /// Forwarding to a closed bridge is a no-op (not an exception). Real
    /// life: the agent received a signallingFrame after the orchestrator
    /// tore down its Cirrus instance.
    /// </summary>
    [Fact]
    public async Task ForwardToClosedBridge_IsNoOp()
    {
        var emptyText = new List<string>();
        var emptyBin = new List<byte[]>();
        var bridge = new SignallingBridge(
            runId: "test-run",
            localCirrusUrl: new Uri("ws://127.0.0.1:1/"),
            sendUpstreamAsync: (_, _, _) => ValueTask.CompletedTask,
            log: NullLogger<SignallingBridge>.Instance);

        // Never called StartAsync — bridge is closed by definition.
        // The call should silently return without throwing.
        await bridge.ForwardToLocalAsync("frame", binary: null, CancellationToken.None);
        await bridge.ForwardToLocalAsync(text: null, binary: new byte[] { 1, 2, 3 }, CancellationToken.None);
        Assert.False(bridge.IsOpen);

        bridge.Dispose();
        Assert.Empty(emptyText);
        Assert.Empty(emptyBin);
    }

    /// <summary>
    /// Connecting to a non-existent local Cirrus URL surfaces the
    /// failure to the caller (so the dispatcher can ack-reject upstream).
    /// </summary>
    [Fact]
    public async Task StartAsync_FailsFast_OnUnreachableCirrus()
    {
        var bridge = new SignallingBridge(
            runId: "test-run",
            // 127.0.0.2:1 — RFC 1122 loopback range, port 1 reliably refused.
            localCirrusUrl: new Uri("ws://127.0.0.2:1/"),
            sendUpstreamAsync: (_, _, _) => ValueTask.CompletedTask,
            log: NullLogger<SignallingBridge>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => bridge.StartAsync(cts.Token));
        bridge.Dispose();
    }

    // ---- helpers ----------------------------------------------------

    static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.True(predicate(), $"predicate did not become true within {timeout.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Test fixture that owns the bridge + its upstream-callback shims.
    /// IAsyncDisposable so xunit cleans up between tests.
    /// </summary>
    sealed class BridgeUnderTest : IAsyncDisposable
    {
        public SignallingBridge Inner { get; }

        public BridgeUnderTest(Uri cirrusUrl, List<string> textSink, List<byte[]> binarySink)
        {
            Inner = new SignallingBridge(
                runId: "test-run",
                localCirrusUrl: cirrusUrl,
                sendUpstreamAsync: (_, binary, text) =>
                {
                    if (binary is { Length: > 0 } b)
                    {
                        lock (binarySink) binarySink.Add(b.ToArray());
                    }
                    else if (!string.IsNullOrEmpty(text))
                    {
                        lock (textSink) textSink.Add(text);
                    }
                    return ValueTask.CompletedTask;
                },
                log: NullLogger<SignallingBridge>.Instance);
        }

        public ValueTask DisposeAsync()
        {
            Inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Minimal HTTP listener that upgrades a single WebSocket and echoes
/// every received frame back unchanged. Each test gets a fresh instance
/// on a random port so the suite parallelises cleanly.
/// </summary>
internal sealed class EchoWebSocketServer : IAsyncDisposable
{
    public Uri Url { get; }
    readonly HttpListener _http;
    readonly CancellationTokenSource _cts = new();
    readonly Task _runTask;

    EchoWebSocketServer(HttpListener http, int port)
    {
        _http = http;
        Url   = new Uri($"ws://127.0.0.1:{port}/");
        _runTask = Task.Run(RunAsync);
    }

    public static Task<EchoWebSocketServer> StartAsync()
    {
        // Bind to a random unused port by trying a few times; on Windows
        // HttpListener doesn't expose port-0 binding directly.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var port = Random.Shared.Next(40_000, 60_000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return Task.FromResult(new EchoWebSocketServer(listener, port));
            }
            catch (HttpListenerException)
            {
                listener.Close();
                // port likely in use; try another
            }
        }
        throw new InvalidOperationException("could not bind a free local port for the echo server");
    }

    async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch (HttpListenerException) { return; }
            catch (ObjectDisposedException) { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            _ = Task.Run(() => HandleAsync(ctx, _cts.Token));
        }
    }

    static async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
        var socket = wsCtx.WebSocket;
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                // Echo the frame back unchanged.
                await socket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException)          { /* peer disconnect; expected */ }
        finally
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", CancellationToken.None);
            }
            catch { /* ignore */ }
            socket.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _http.Stop(); _http.Close(); } catch { /* ignore */ }
        try { await _runTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
