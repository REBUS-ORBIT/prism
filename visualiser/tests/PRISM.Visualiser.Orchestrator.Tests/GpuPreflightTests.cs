using System.Collections.Generic;
using System.Runtime.Versioning;

using Serilog;

using Xunit;

using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Phase K hardening: <see cref="GpuPreflight"/> exercised against a
/// fully stubbed <see cref="GpuPreflight.IGpuProbe"/> so the tests
/// don't depend on NVENC, nvidia-smi, or an existing Unreal install.
/// </summary>
[SupportedOSPlatform("windows")]
public class GpuPreflightTests
{
    [Fact]
    public void ParseFirstGpuVramMb_handles_canonical_nvidia_smi_output()
    {
        // `nvidia-smi --query-gpu=memory.free --format=csv,noheader,nounits`
        // emits one MB integer per GPU, newline-separated.
        Assert.Equal(8192, GpuPreflight.ParseFirstGpuVramMb("8192\n"));
        Assert.Equal(8192, GpuPreflight.ParseFirstGpuVramMb("8192\n4096\n"));
    }

    [Fact]
    public void ParseFirstGpuVramMb_strips_trailing_units_defensively()
    {
        // Some driver builds still emit units even with --format=...,nounits.
        Assert.Equal(2048, GpuPreflight.ParseFirstGpuVramMb("2048 MiB\n"));
    }

    [Fact]
    public void ParseFirstGpuVramMb_returns_null_on_empty_or_garbage()
    {
        Assert.Null(GpuPreflight.ParseFirstGpuVramMb(string.Empty));
        Assert.Null(GpuPreflight.ParseFirstGpuVramMb("\n\n"));
        Assert.Null(GpuPreflight.ParseFirstGpuVramMb("error: NVIDIA-SMI has failed"));
    }

    [Fact]
    public void Check_passes_when_vram_above_minimum_and_no_stale_editor()
    {
        var probe = new FakeProbe(freeVramGb: 8.0);
        var preflight = new GpuPreflight(NoOpLog(), probe);

        var result = preflight.Check();

        Assert.True(result.Ok);
        Assert.Null(result.Reason);
        Assert.Equal(8.0, result.FreeVramGb);
    }

    [Fact]
    public void Check_fails_when_vram_below_minimum()
    {
        var probe = new FakeProbe(freeVramGb: 2.5);
        var preflight = new GpuPreflight(NoOpLog(), probe) { MinFreeVramGb = 4.0 };

        var result = preflight.Check();

        Assert.False(result.Ok);
        Assert.Contains("insufficient free VRAM", result.Reason);
        Assert.Equal(2.5, result.FreeVramGb);
    }

    [Fact]
    public void Check_fails_when_stale_editor_process_running()
    {
        var probe = new FakeProbe(freeVramGb: 16.0, runningEditors: new[] { "UnrealEditor" });
        var preflight = new GpuPreflight(NoOpLog(), probe);

        var result = preflight.Check();

        Assert.False(result.Ok);
        Assert.Contains("stale Unreal editor process", result.Reason);
        Assert.Equal(16.0, result.FreeVramGb);
    }

    [Fact]
    public void Check_soft_warns_when_nvidia_smi_missing_and_strict_false()
    {
        var probe = new FakeProbe(freeVramGb: null);
        var preflight = new GpuPreflight(NoOpLog(), probe, strict: false);

        var result = preflight.Check();

        Assert.True(result.Ok);
        Assert.Null(result.FreeVramGb);
    }

    [Fact]
    public void Check_hard_rejects_when_nvidia_smi_missing_and_strict_true()
    {
        var probe = new FakeProbe(freeVramGb: null);
        var preflight = new GpuPreflight(NoOpLog(), probe, strict: true);

        var result = preflight.Check();

        Assert.False(result.Ok);
        Assert.Contains("nvidia-smi", result.Reason);
        Assert.Null(result.FreeVramGb);
    }

    [Fact]
    public void Check_picks_up_custom_editor_prefix()
    {
        // A workstation with a forked UE build may have a renamed
        // editor binary; the prefix is configurable.
        var probe = new FakeProbe(freeVramGb: 8.0, runningEditors: new[] { "PrismCustomEditor" });
        var preflight = new GpuPreflight(NoOpLog(), probe)
        {
            EditorProcessPrefix = "PrismCustomEditor",
        };

        var result = preflight.Check();

        Assert.False(result.Ok);
        Assert.Contains("stale Unreal editor process", result.Reason);
    }

    [Fact]
    public void FailureCode_and_ExitCode_are_stable()
    {
        // These values are documented in the OpenAPI spec and the
        // PORTAL_INTEGRATION.md error table — locking them down with
        // a test so a careless rename surfaces immediately.
        Assert.Equal("gpu_preflight_failed", GpuPreflight.FailureCode);
        Assert.Equal(10, GpuPreflight.ExitCode);
    }

    // ----------------------------------------------------------------

    private static ILogger NoOpLog() => new LoggerConfiguration().CreateLogger();

    private sealed class FakeProbe : GpuPreflight.IGpuProbe
    {
        private readonly double? _freeVramGb;
        private readonly IReadOnlyList<string> _editors;

        public FakeProbe(double? freeVramGb, IEnumerable<string>? runningEditors = null)
        {
            _freeVramGb = freeVramGb;
            _editors = runningEditors is null
                ? System.Array.Empty<string>()
                : new List<string>(runningEditors);
        }

        public double? TryGetFirstGpuFreeVramGb() => _freeVramGb;

        public IReadOnlyList<string> GetRunningEditorProcessNames(string namePrefix)
            => _editors;
    }
}
