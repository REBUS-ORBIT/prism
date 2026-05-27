using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;

using Serilog;

using Xunit;

using PRISM.Visualiser.Orchestrator.PixelStreaming;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 15 — <see cref="PixelStreamingSession.ShutdownAsync(IShutdownTarget, IShutdownTarget, TimeSpan, ILogger, CancellationToken)"/>
/// kills UE BEFORE Cirrus and waits for each to exit before moving to
/// the next. Asserted via a fake <see cref="IShutdownTarget"/> that
/// records the order it received <c>Kill</c> + <c>WaitForExitAsync</c>
/// calls.
///
/// <para>
/// We don't spawn real processes — the contract under test is purely
/// the ordering + wait-for-exit semantics of the static cleanup
/// helper. The same helper is what the production
/// <see cref="PixelStreamingSession.ShutdownAsync(CancellationToken)"/>
/// instance method calls through to.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class PixelStreamingSessionShutdownTests
{
    [Fact]
    public async Task ShutdownAsync_KillsUeBeforeCirrus()
    {
        var orderLog = new ConcurrentQueue<string>();
        var ue = new FakeShutdownTarget(pid: 100, label: "ue", orderLog: orderLog);
        var cirrus = new FakeShutdownTarget(pid: 200, label: "cirrus", orderLog: orderLog);
        var log = NullLogger();

        await PixelStreamingSession.ShutdownAsync(
            ue, cirrus, grace: TimeSpan.FromSeconds(1), log);

        var actions = orderLog.ToArray();
        // The exact sequence we contractually require:
        //   1) ue.Kill   2) ue.WaitForExit
        //   3) cirrus.Kill   4) cirrus.WaitForExit
        Assert.Equal(new[] { "ue:Kill", "ue:Wait", "cirrus:Kill", "cirrus:Wait" }, actions);

        // Each handle saw exactly one Kill + one Wait.
        Assert.Equal(1, ue.KillCount);
        Assert.Equal(1, ue.WaitCount);
        Assert.Equal(1, cirrus.KillCount);
        Assert.Equal(1, cirrus.WaitCount);

        // Plan: "Assert both processes' JobObject memberships are
        // released" — i.e. each process exited (which is the only way
        // Win32 releases a job-object membership; the API has no
        // explicit RemoveProcess call). Our fakes flip HasExited the
        // moment Kill is called.
        Assert.True(ue.HasExited);
        Assert.True(cirrus.HasExited);
    }

    [Fact]
    public async Task ShutdownAsync_UeFirst_EvenWhenAlreadyExited()
    {
        // If UE has already exited naturally (e.g. user closed the
        // PIE window) we should NOT call Kill on it again. Cirrus is
        // still alive; we kill it.
        var orderLog = new ConcurrentQueue<string>();
        var ue = new FakeShutdownTarget(pid: 100, label: "ue", orderLog: orderLog) { HasExited = true };
        var cirrus = new FakeShutdownTarget(pid: 200, label: "cirrus", orderLog: orderLog);
        var log = NullLogger();

        await PixelStreamingSession.ShutdownAsync(
            ue, cirrus, grace: TimeSpan.FromSeconds(1), log);

        Assert.Equal(0, ue.KillCount);          // skipped — already exited
        Assert.Equal(1, ue.WaitCount);          // still waited for clean exit
        Assert.Equal(1, cirrus.KillCount);
        Assert.Equal(1, cirrus.WaitCount);

        var actions = orderLog.ToArray();
        Assert.Equal(new[] { "ue:Wait", "cirrus:Kill", "cirrus:Wait" }, actions);
    }

    [Fact]
    public async Task ShutdownAsync_TolerantOfHangingTargets()
    {
        // A Cirrus that never honours Kill (the WaitForExit task
        // hangs) must NOT block the shutdown forever; the grace
        // period elapses and we move on. The JobObject KILL_ON_JOB_CLOSE
        // backstop then reclaims the process in production.
        var orderLog = new ConcurrentQueue<string>();
        var ue = new FakeShutdownTarget(pid: 100, label: "ue", orderLog: orderLog);
        var cirrus = new HangingShutdownTarget(pid: 200, label: "cirrus", orderLog: orderLog);
        var log = NullLogger();

        var sw = Stopwatch.StartNew();
        await PixelStreamingSession.ShutdownAsync(
            ue, cirrus, grace: TimeSpan.FromMilliseconds(250), log);
        sw.Stop();

        // Must return within ~2× grace, NOT block on the hanging target.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"shutdown took too long: {sw.Elapsed.TotalMilliseconds:F0}ms");

        // Cirrus still got its Kill call, just didn't observe an exit.
        Assert.Equal(1, ue.KillCount);
        Assert.Equal(1, cirrus.KillCount);
    }

    [Fact]
    public async Task ShutdownAsync_NullArgsThrow()
    {
        var ok = new FakeShutdownTarget(1, "x", new ConcurrentQueue<string>());
        var log = NullLogger();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PixelStreamingSession.ShutdownAsync(null!, ok, TimeSpan.FromSeconds(1), log));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PixelStreamingSession.ShutdownAsync(ok, null!, TimeSpan.FromSeconds(1), log));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            PixelStreamingSession.ShutdownAsync(ok, ok, TimeSpan.FromSeconds(1), null!));
    }

    private static ILogger NullLogger() =>
        new LoggerConfiguration().CreateLogger();

    /// <summary>
    /// Test double — records every <c>Kill</c> / <c>WaitForExitAsync</c>
    /// call into a shared queue so the test can assert on global
    /// ordering. <c>HasExited</c> flips to <see langword="true"/> the
    /// moment <c>Kill</c> is called, mirroring Win32's
    /// <c>TerminateProcess</c> semantics.
    /// </summary>
    private sealed class FakeShutdownTarget : IShutdownTarget
    {
        private readonly ConcurrentQueue<string> _orderLog;
        private readonly string _label;

        public FakeShutdownTarget(int pid, string label, ConcurrentQueue<string> orderLog)
        {
            ProcessId = pid;
            _label = label;
            _orderLog = orderLog;
        }

        public int ProcessId { get; }
        public bool HasExited { get; set; }
        public int KillCount { get; private set; }
        public int WaitCount { get; private set; }

        public void Kill()
        {
            KillCount++;
            HasExited = true;
            _orderLog.Enqueue($"{_label}:Kill");
        }

        public Task WaitForExitAsync(CancellationToken ct)
        {
            WaitCount++;
            _orderLog.Enqueue($"{_label}:Wait");
            HasExited = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Test double that ignores <c>Kill</c> and blocks
    /// <c>WaitForExitAsync</c> until cancellation. Used to verify the
    /// shutdown helper enforces its grace period.
    /// </summary>
    private sealed class HangingShutdownTarget : IShutdownTarget
    {
        private readonly ConcurrentQueue<string> _orderLog;
        private readonly string _label;

        public HangingShutdownTarget(int pid, string label, ConcurrentQueue<string> orderLog)
        {
            ProcessId = pid;
            _label = label;
            _orderLog = orderLog;
        }

        public int ProcessId { get; }
        public bool HasExited => false;
        public int KillCount { get; private set; }

        public void Kill()
        {
            KillCount++;
            _orderLog.Enqueue($"{_label}:Kill");
        }

        public Task WaitForExitAsync(CancellationToken ct)
        {
            _orderLog.Enqueue($"{_label}:WaitStart");
            return Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }
}
