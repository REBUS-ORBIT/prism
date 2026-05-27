using System.Runtime.Versioning;

using Xunit;

using PRISM.Visualiser.Orchestrator.PixelStreaming;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 13 — <see cref="SignallingSupervisor"/>'s ready-line
/// parser detects the Cirrus listening announcement in a stream of
/// noisy stdout lines without ever launching a real process.
///
/// <para>
/// The PixelStreaming 2 signalling server has shipped at least three
/// log shapes across UE 5.5 / 5.6 / 5.7 (see
/// <see cref="SignallingSupervisor.ReadyLinePattern"/> docs); the
/// parser is permissive enough to match all of them. We assert
/// against each variant separately so a regression to a stricter
/// regex is caught.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class SignallingSupervisorTests
{
    [Theory]
    [InlineData("Listening on :8888", 8888)]
    [InlineData("WebSocketServer started, listening on port 8888", 8888)]
    [InlineData("HTTP server listening on port 9000", 9000)]
    [InlineData("[2026-05-27T18:32:09.123Z] info Listening on :8888", 8888)]
    [InlineData("Streamer port: 8889 | Listening on port 8888", 8888)]
    public void TryParseReadyLine_ExtractsPortFromKnownShapes(string line, int expected)
    {
        Assert.True(SignallingSupervisor.TryParseReadyLine(line, out var port));
        Assert.Equal(expected, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Cirrus boot: loading config...")]
    [InlineData("connection upgrade failed")]
    [InlineData("Disconnecting peer")]
    [InlineData("HTTPS server starting")] // no port → no match
    public void TryParseReadyLine_ReturnsFalseOnUnrelatedLines(string line)
    {
        Assert.False(SignallingSupervisor.TryParseReadyLine(line, out _));
    }

    [Theory]
    [InlineData("Streamer connected: orbit_5b9c1d4f", "orbit_5b9c1d4f")]
    [InlineData("info Streamer connected: orbit_abc123def", "orbit_abc123def")]
    [InlineData("[2026-05-27] Streamer registered: streamer-7", "streamer-7")]
    public void TryParseStreamerConnected_ExtractsIdFromKnownShapes(string line, string expected)
    {
        Assert.True(SignallingSupervisor.TryParseStreamerConnected(line, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("Player connected: PlayerA")]   // a Player, not a Streamer
    [InlineData("Listening on :8888")]
    public void TryParseStreamerConnected_ReturnsFalseOnUnrelatedLines(string line)
    {
        Assert.False(SignallingSupervisor.TryParseStreamerConnected(line, out _));
    }

    [Fact]
    public async Task AwaitReadyAsync_ExtractsPortFromNoisyStream()
    {
        // Smoke Test 13 — the parser must find the ready line inside
        // a stream of unrelated Cirrus noise. We feed it via an
        // IAsyncEnumerable<string> so this never touches a process.
        var lines = new[]
        {
            "Cirrus boot: loading config...",
            "Reading signalling-server-config.json",
            "TLS disabled (use --HTTPS to enable)",
            "Streamer port: 8889",
            "WebSocketServer started, listening on port 8888",
            "Awaiting streamer connection...",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var port = await SignallingSupervisor.AwaitReadyAsync(ToAsync(lines), cts.Token);

        Assert.Equal(8888, port);
    }

    [Fact]
    public async Task AwaitReadyAsync_ThrowsWhenStreamCompletesWithoutMatch()
    {
        var lines = new[]
        {
            "Cirrus boot: loading config...",
            "config parse failed",
            "exiting",
        };

        await Assert.ThrowsAsync<SignallingStartException>(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SignallingSupervisor.AwaitReadyAsync(ToAsync(lines), cts.Token);
        });
    }

    [Fact]
    public async Task AwaitReadyAsync_HonoursCancellation()
    {
        // TaskCanceledException is a derived OperationCanceledException —
        // either is acceptable; we just want to see the cancellation
        // propagate instead of the parser stalling.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await SignallingSupervisor.AwaitReadyAsync(BlockingAsync(cts.Token), cts.Token);
        });
    }

    [Fact]
    public async Task AwaitStreamerConnectedAsync_ExtractsStreamerId()
    {
        var lines = new[]
        {
            "WebSocketServer started, listening on port 8888",
            "Awaiting streamer connection...",
            "info Streamer connected: orbit_5b9c1d4f",
            "broadcasting offer",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var id = await SignallingSupervisor.AwaitStreamerConnectedAsync(ToAsync(lines), cts.Token);

        Assert.Equal("orbit_5b9c1d4f", id);
    }

    [Fact]
    public void Resolve_NoUeRoot_ReturnsEmptyResult()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "phase-f-no-ue-" + Guid.NewGuid().ToString("N")[..8]);
        var result = SignallingSupervisor.Resolve(nonexistent);

        Assert.Null(result.CirrusScriptPath);
        Assert.Null(result.NodeExePath);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void Resolve_HonoursEnvVarOverrides()
    {
        // Plan §Verification optional smoke: a synthetic "fake Cirrus"
        // path can be wired via env-var overrides. We write a real
        // file on disk so SignallingSupervisor.Resolve's File.Exists
        // probe is satisfied.
        var tmpDir = Path.Combine(Path.GetTempPath(), "phase-f-resolve-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        var fakeScript = Path.Combine(tmpDir, "fake-cirrus.js");
        var fakeNode = Path.Combine(tmpDir, "fake-node.exe");
        File.WriteAllText(fakeScript, "// no-op");
        File.WriteAllBytes(fakeNode, new byte[] { 0x4D, 0x5A });

        var oldScript = Environment.GetEnvironmentVariable(SignallingSupervisor.EnvVarCirrusScript);
        var oldNode = Environment.GetEnvironmentVariable(SignallingSupervisor.EnvVarNodeExe);
        try
        {
            Environment.SetEnvironmentVariable(SignallingSupervisor.EnvVarCirrusScript, fakeScript);
            Environment.SetEnvironmentVariable(SignallingSupervisor.EnvVarNodeExe, fakeNode);

            var result = SignallingSupervisor.Resolve(@"C:\does-not-exist");

            Assert.Equal(fakeScript, result.CirrusScriptPath);
            Assert.Equal(fakeNode, result.NodeExePath);
            Assert.True(result.IsComplete);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SignallingSupervisor.EnvVarCirrusScript, oldScript);
            Environment.SetEnvironmentVariable(SignallingSupervisor.EnvVarNodeExe, oldNode);
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async IAsyncEnumerable<string> ToAsync(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            await Task.Yield();
            yield return line;
        }
    }

    private static async IAsyncEnumerable<string> BlockingAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Stream that never yields. Used to make sure
        // AwaitReadyAsync honours its CancellationToken even when
        // no lines arrive — the Task.Delay below picks up the
        // cancellation immediately rather than burning the test
        // for 30 s of wall-clock time.
        await Task.Yield();
        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        yield return "should never get here";
    }
}
