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
    // Pattern 1 — UE-side canonical OnJoined event (UE 5.7 / Wilbur).
    [InlineData(
        "[2026.05.28-12.15.01:178][  0]LogPixelStreaming2EpicRtc: RoomSignallingContextObserver::OnJoined. Local participant joined the room. roomId=[orbit_cb4d2125] localParticipantId=[orbit_cb4d2125] state=[Joined]",
        "orbit_cb4d2125", "OnJoined")]
    // Pattern 2 — UE-side simpler "Player joined" event.
    [InlineData(
        "[2026.05.28-12.15.01:182][  0]LogPixelStreaming2RTC: Player (orbit_cb4d2125) joined",
        "orbit_cb4d2125", "PlayerJoined")]
    // Pattern 3 — Wilbur-side endpointIdConfirm (committed id).
    [InlineData(
        "info: < orbit_cb4d2125 :: {\"type\":\"endpointIdConfirm\",\"committedId\":\"orbit_cb4d2125\"}",
        "orbit_cb4d2125", "EndpointIdConfirm")]
    // Pattern 4 — Wilbur-side endpointId send (UE -> Wilbur introduction).
    [InlineData(
        "info: > UnknownStreamer :: {\"id\":\"orbit_cb4d2125\",\"protocolVersion\":\"1.1.0\",\"type\":\"endpointId\"}",
        "orbit_cb4d2125", "EndpointId")]
    // Pattern 5 — legacy Cirrus fallback (kept for graceful degradation).
    [InlineData(
        "Streamer connected: orbit_5b9c1d4f",
        "orbit_5b9c1d4f", "LegacyCirrus")]
    [InlineData(
        "[2026-05-27] Streamer registered: streamer-7",
        "streamer-7", "LegacyCirrus")]
    public void TryParseStreamerConnected_RecognisesAllSupportedShapes(
        string line, string expectedId, string expectedPattern)
    {
        Assert.True(SignallingSupervisor.TryParseStreamerConnected(
            line, out var id, out var pattern));
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedPattern, pattern);
    }

    [Theory]
    // Bare OnJoined without `localParticipantId=[...]` tail should still
    // fire (canonical signal is `state=[Joined]`); id is empty.
    [InlineData(
        "[2026.05.28-12.15.01:178][  0]LogPixelStreaming2EpicRtc: RoomSignallingContextObserver::OnJoined. state=[Joined]",
        "OnJoined")]
    public void TryParseStreamerConnected_EmptyIdStillFiresOnCanonicalSignal(
        string line, string expectedPattern)
    {
        Assert.True(SignallingSupervisor.TryParseStreamerConnected(
            line, out var id, out var pattern));
        Assert.Equal(string.Empty, id);
        Assert.Equal(expectedPattern, pattern);
    }

    [Theory]
    [InlineData("Player connected: PlayerA")]   // legacy "player connected", not streamer
    [InlineData("Listening on :8888")]
    [InlineData("info: New streamer connection: ::ffff:127.0.0.1")] // pre-handshake noise
    [InlineData("info: < UnknownStreamer :: {\"type\":\"identify\"}")] // identify, not endpointId(Confirm)
    [InlineData("info: < UnknownStreamer :: {\"type\":\"config\",\"peerConnectionOptions\":{}}")] // config, not endpointId(Confirm)
    [InlineData("info: > orbit_cb4d2125 :: {\"type\":\"ping\",\"time\":1780053331}")] // ping, not registration
    [InlineData("LogPixelStreaming2RTC: Player joined the room.")] // no parens / id
    [InlineData("LogPixelStreaming2EpicRtc: state=[Joining]")] // wrong state
    [InlineData("Some random LogTemp: line about a player")] // wrong log channel
    public void TryParseStreamerConnected_RejectsUnrelatedLines(string line)
    {
        Assert.False(SignallingSupervisor.TryParseStreamerConnected(
            line, out _, out _));
    }

    [Fact]
    public void StreamerConnectedPatterns_OnJoinedIsFirst()
    {
        // Plan §2: the canonical UE-side "RoomSignallingContextObserver::OnJoined"
        // event is the primary match — keep it at index 0 so the orchestrator's
        // diagnostic reports the canonical shape whenever both UE and Wilbur
        // streams observe the registration.
        Assert.NotEmpty(SignallingSupervisor.StreamerConnectedPatterns);
        Assert.Equal("OnJoined", SignallingSupervisor.StreamerConnectedPatterns[0].Name);
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
    public async Task AwaitStreamerConnectedAsync_LegacyCirrusFallbackStillFires()
    {
        var lines = new[]
        {
            "WebSocketServer started, listening on port 8888",
            "Awaiting streamer connection...",
            "info Streamer connected: orbit_5b9c1d4f",
            "broadcasting offer",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var match = await SignallingSupervisor.AwaitStreamerConnectedAsync(
            ToAsync(lines), cts.Token);

        Assert.Equal("orbit_5b9c1d4f", match.StreamerId);
        Assert.Equal("LegacyCirrus", match.MatchedPattern);
    }

    [Fact]
    public async Task AwaitStreamerConnectedAsync_FiresOnCanonicalOnJoined_FromUeStdout()
    {
        // Captured shape from the PC01 v0.3.8 run that timed out at
        // 120s on the legacy regex. The UE-side log is the canonical
        // "streamer registered" signal in PS2 / Wilbur — Wilbur itself
        // never emits this string. v0.3.9 must fire on the OnJoined
        // line within ~one yield of arrival.
        var lines = new[]
        {
            "[2026.05.28-12.14.42:000][  0]LogInit: Build: ++UE+Release-5.7-CL-XXXXXXX",
            "[2026.05.28-12.14.45:123][  0]LogPixelStreaming2: Initialised PixelStreaming2 module",
            "[2026.05.28-12.15.01:177][  0]LogEpicRtcWebsocket: Websocket connection made to: ws://127.0.0.1:52572",
            "[2026.05.28-12.15.01:178][  0]LogPixelStreaming2EpicRtc: RoomSignallingContextObserver::OnJoined. Local participant joined the room. roomId=[orbit_cb4d2125] localParticipantId=[orbit_cb4d2125] state=[Joined]",
            "[2026.05.28-12.15.01:182][  0]LogPixelStreaming2RTC: Player (orbit_cb4d2125) joined",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var match = await SignallingSupervisor.AwaitStreamerConnectedAsync(
            ToAsync(lines), cts.Token);

        // OnJoined is the FIRST registered pattern, so when the
        // canonical UE line arrives before any Wilbur signal the
        // orchestrator must attribute the match to it.
        Assert.Equal("OnJoined", match.MatchedPattern);
        Assert.Equal("orbit_cb4d2125", match.StreamerId);
    }

    [Fact]
    public async Task AwaitStreamerConnectedAsync_FiresOnWilburEndpointIdConfirm()
    {
        // When the UE-side `OnJoined` line is filtered or unavailable
        // (e.g. UE log verbosity tweaks suppress it in a future
        // patch), Wilbur's `endpointIdConfirm` must still satisfy the
        // matcher so the orchestrator doesn't time out.
        var lines = new[]
        {
            "info: New streamer connection: ::ffff:127.0.0.1",
            "info: < UnknownStreamer :: {\"type\":\"identify\"}",
            "info: < UnknownStreamer :: {\"type\":\"config\",\"peerConnectionOptions\":{}}",
            "info: > UnknownStreamer :: {\"id\":\"orbit_cb4d2125\",\"protocolVersion\":\"1.1.0\",\"type\":\"endpointId\"}",
            "info: < orbit_cb4d2125 :: {\"type\":\"endpointIdConfirm\",\"committedId\":\"orbit_cb4d2125\"}",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var match = await SignallingSupervisor.AwaitStreamerConnectedAsync(
            ToAsync(lines), cts.Token);

        // Pattern 4 (`EndpointId`) sits ahead of `EndpointIdConfirm`
        // in `StreamerConnectedPatterns` (ID-out before ID-in is
        // confirmed); that line arrives first in the captured
        // sequence, so it should win the race.
        Assert.Equal("EndpointId", match.MatchedPattern);
        Assert.Equal("orbit_cb4d2125", match.StreamerId);
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

    [Fact]
    public void Resolve_PrefersWilburEntrypoint_OverLegacyCirrusCandidates()
    {
        // PS2 (UE 5.5+) ships wilbur at SignallingWebServer\dist\index.js.
        // The legacy index.js Cirrus candidate sits one level up; the
        // resolver must always prefer wilbur when both exist.
        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            "phase-f-wilbur-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var wilburPath = Path.Combine(tmpDir, SignallingSupervisor.WilburEntrypointRelative);
            var legacyDir = Path.Combine(tmpDir, SignallingSupervisor.SignallingWebServerRelative);
            var legacyPath = Path.Combine(legacyDir, "index.js");
            var nodePath = Path.Combine(tmpDir, SignallingSupervisor.WilburNodeExeRelative);

            Directory.CreateDirectory(Path.GetDirectoryName(wilburPath)!);
            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(Path.GetDirectoryName(nodePath)!);

            File.WriteAllText(wilburPath, "// wilbur\n");
            File.WriteAllText(legacyPath, "// legacy cirrus\n");
            File.WriteAllBytes(nodePath, new byte[] { 0x4D, 0x5A });

            var result = SignallingSupervisor.Resolve(tmpDir);

            Assert.Equal(wilburPath, result.CirrusScriptPath);
            Assert.Equal(nodePath, result.NodeExePath);
            Assert.True(result.IsWilbur);
            Assert.True(result.IsComplete);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Resolve_FallsBackToLegacyCirrus_WhenWilburMissing()
    {
        var tmpDir = Path.Combine(
            Path.GetTempPath(),
            "phase-f-cirrus-fb-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var legacyDir = Path.Combine(tmpDir, SignallingSupervisor.SignallingWebServerRelative);
            var legacyPath = Path.Combine(legacyDir, "Cirrus.js");
            var nodePath = Path.Combine(tmpDir, SignallingSupervisor.NodeExeRelative);

            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(Path.GetDirectoryName(nodePath)!);

            File.WriteAllText(legacyPath, "// legacy cirrus\n");
            File.WriteAllBytes(nodePath, new byte[] { 0x4D, 0x5A });

            var result = SignallingSupervisor.Resolve(tmpDir);

            Assert.Equal(legacyPath, result.CirrusScriptPath);
            Assert.Equal(nodePath, result.NodeExePath);
            Assert.False(result.IsWilbur);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void BuildStartInfo_Wilbur_EmitsCommanderStyleArgs()
    {
        var resolved = new SignallingResolveResult(
            CirrusScriptPath: @"C:\fake\SignallingWebServer\dist\index.js",
            NodeExePath: @"C:\fake\node.exe",
            IsWilbur: true);

        var psi = SignallingSupervisor.BuildStartInfo(resolved, playerPort: 65000, streamerPort: 65001);

        Assert.Equal(@"C:\fake\node.exe", psi.FileName);
        // Working directory should be the wilbur package root (one
        // level above dist\index.js), so wilbur's config.json /
        // relative paths resolve correctly.
        Assert.Equal(@"C:\fake\SignallingWebServer", psi.WorkingDirectory);
        var args = psi.ArgumentList.ToArray();
        Assert.Equal(@"C:\fake\SignallingWebServer\dist\index.js", args[0]);
        Assert.Contains("--player_port=65000", args);
        Assert.Contains("--streamer_port=65001", args);
        Assert.Contains("--serve", args);
        Assert.Contains("--console_messages", args);
        Assert.Contains("verbose", args);
        Assert.Contains("--log_config", args);
        // Legacy --HttpPort= must NOT appear — wilbur doesn't
        // understand it and prints a "Unknown option" complaint.
        Assert.DoesNotContain(args, a => a.StartsWith("--HttpPort", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildStartInfo_LegacyCirrus_EmitsHttpPortArg()
    {
        var resolved = new SignallingResolveResult(
            CirrusScriptPath: @"C:\legacy\SignallingWebServer\cirrus.js",
            NodeExePath: @"C:\legacy\node.exe",
            IsWilbur: false);

        var psi = SignallingSupervisor.BuildStartInfo(resolved, playerPort: 8888, streamerPort: 8888);

        Assert.Equal(@"C:\legacy\node.exe", psi.FileName);
        Assert.Equal(@"C:\legacy\SignallingWebServer", psi.WorkingDirectory);
        var args = psi.ArgumentList.ToArray();
        Assert.Equal(@"C:\legacy\SignallingWebServer\cirrus.js", args[0]);
        Assert.Contains("--HttpPort=8888", args);
        Assert.DoesNotContain(args, a => a.StartsWith("--player_port", StringComparison.Ordinal));
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
