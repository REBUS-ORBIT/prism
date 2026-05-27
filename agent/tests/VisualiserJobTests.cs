using PRISM.Agent.Pipeline;
using Xunit;

namespace PRISM.Agent.Tests;

/// <summary>
/// Coverage for the static / pure helpers on
/// <see cref="VisualiserJob"/>. The full spawn-and-pump flow is a
/// non-trivial integration surface (depends on the real orchestrator
/// EXE, .NET runtime resolution, and a live PRISM server) so it's
/// validated end-to-end on a workstation after the v0.3.0 release
/// rather than here.
/// </summary>
public sealed class VisualiserJobTests
{
    [Theory]
    [InlineData("https://orbit.rebus.industries",      "prod")]
    [InlineData("https://orbit.rebus.industries/",     "prod")]
    [InlineData("https://orbit-dev.rebus.industries",  "dev")]
    [InlineData("https://orbit-dev.rebus.industries/", "dev")]
    [InlineData("",                                    "prod")]
    [InlineData(null,                                  "prod")]
    [InlineData("not-a-url-but-orbit-dev-somewhere",   "dev")]
    [InlineData("https://elsewhere.example.com",       "prod")]
    public void ResolveServerSelector_MapsHostToSelector(string? input, string expected)
    {
        Assert.Equal(expected, VisualiserJob.ResolveServerSelector(input));
    }

    [Theory]
    [InlineData(0, 8888)]
    [InlineData(1, 8898)]
    [InlineData(2, 8908)]
    [InlineData(20, 8888)]   // wraps via modulo 20
    [InlineData(21, 8898)]   // 21 % 20 == 1
    [InlineData(-5, 8888)]   // negatives clamp to zero offset
    public void ResolveSignallingPortHint_StaysInSafeRange(int slot, int expectedHint)
    {
        Assert.Equal(expectedHint, VisualiserJob.ResolveSignallingPortHint(slot));
    }

    [Fact]
    public void OrchestratorExeName_MatchesOrchestratorCsprojAssemblyName()
    {
        // Sanity guard: the orchestrator project publishes as
        // `prism-visualiser.exe` per its csproj `AssemblyName`
        // (the project FILENAME is PRISM.Visualiser.Orchestrator.csproj,
        // but the OUTPUT name was deliberately shortened to match the
        // CLI banner). If the csproj ever flips the assembly name,
        // VisualiserJob's path probe will silently miss the bundled
        // binary on every workstation. Pin both the current and the
        // legacy fallback names here so any rename surfaces as a
        // failing test in CI.
        Assert.Equal("prism-visualiser.exe", VisualiserJob.OrchestratorExeName);
        Assert.Equal("PRISM.Visualiser.Orchestrator.exe", VisualiserJob.OrchestratorExeNameLegacy);
        Assert.Equal("Visualiser", VisualiserJob.OrchestratorSubDir);
        Assert.Equal("PRISM_VISUALISER_ORCHESTRATOR_PATH", VisualiserJob.OrchestratorPathEnvVar);
    }
}
