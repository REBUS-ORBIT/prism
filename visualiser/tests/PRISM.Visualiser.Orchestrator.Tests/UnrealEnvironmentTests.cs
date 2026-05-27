using System.Collections.Generic;
using System.Runtime.Versioning;

using Xunit;

using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 7 — <see cref="UnrealEnvironment.TryResolve"/> probes
/// (env var, default path, registry) under a fully stubbed
/// <see cref="UnrealEnvironment.IEnvironmentProbe"/>. No real UE
/// install is required.
///
/// <para>
/// UE-dependent end-to-end coverage (spawning UnrealEditor-Cmd.exe and
/// asserting on the python-emitted ready marker) is intentionally
/// out of scope for CI: GitHub-hosted runners don't have UE installed
/// and the artist-populated <c>v1.0.0-ue5.7</c> template milestone is
/// a Phase D follow-up. These three smoke tests are the entire Phase E
/// automated coverage we ship.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public class UnrealEnvironmentTests
{
    [Fact]
    public void EnvVar_PointsAtValidRoot_ResolvesViaEnvironmentSource()
    {
        const string root = @"C:\Custom\UE_5.7";
        var probe = new FakeProbe
        {
            EnvVars = { [UnrealEnvironment.EnvVarName] = root },
            Directories = { root },
            Files = { System.IO.Path.Combine(root, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe") },
        };

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.NotNull(install);
        Assert.Equal(root, install!.Root);
        Assert.Equal(UnrealResolutionSource.EnvironmentVariable, install.Source);
        Assert.True(System.IO.Path.IsPathRooted(install.EditorCmdPath));
    }

    [Fact]
    public void EnvVar_PointsAtMissingDirectory_FallsThroughOtherProbes_AndEnvVarSetReportsTrue()
    {
        const string ghostRoot = @"C:\does-not-exist";
        var probe = new FakeProbe
        {
            EnvVars = { [UnrealEnvironment.EnvVarName] = ghostRoot },
            // No directories / files registered → every probe misses.
        };

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.Null(install);
        Assert.True(UnrealEnvironment.EnvVarSet(probe),
            "EnvVarSet must distinguish 'env var configured but invalid' " +
            "from 'env var unset' so the CLI can emit ue_root_not_found.");
    }

    [Fact]
    public void EnvVar_Unset_AndDefaultPathMissing_AndRegistryMissing_ReturnsNull()
    {
        var probe = new FakeProbe(); // empty everywhere

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.Null(install);
        Assert.False(UnrealEnvironment.EnvVarSet(probe));
    }

    [Fact]
    public void RegistryProvidesValidRoot_WhenEnvAndDefaultPathMiss()
    {
        const string regRoot = @"D:\EpicInstalls\UE_5.7";
        var probe = new FakeProbe
        {
            RegistryValues =
            {
                [(UnrealEnvironment.RegistryKeyPath, UnrealEnvironment.RegistryValueName)] = regRoot,
            },
            Directories = { regRoot },
            Files = { System.IO.Path.Combine(regRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe") },
        };

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.NotNull(install);
        Assert.Equal(regRoot, install!.Root);
        Assert.Equal(UnrealResolutionSource.Registry, install.Source);
    }

    [Fact]
    public void DefaultInstallPath_TakesPrecedenceOverRegistry_WhenBothValid()
    {
        const string defaultRoot = UnrealEnvironment.DefaultInstallRoot;
        const string regRoot = @"D:\OtherUE";
        var probe = new FakeProbe
        {
            Directories = { defaultRoot, regRoot },
            Files =
            {
                System.IO.Path.Combine(defaultRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe"),
                System.IO.Path.Combine(regRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe"),
            },
            RegistryValues =
            {
                [(UnrealEnvironment.RegistryKeyPath, UnrealEnvironment.RegistryValueName)] = regRoot,
            },
        };

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.NotNull(install);
        Assert.Equal(UnrealResolutionSource.DefaultPath, install!.Source);
        Assert.Equal(defaultRoot, install.Root);
    }

    /// <summary>
    /// In-memory probe that records env vars, directories, files, and
    /// registry values. <c>TryResolve</c> sees only what we tell it to.
    /// </summary>
    private sealed class FakeProbe : UnrealEnvironment.IEnvironmentProbe
    {
        public Dictionary<string, string> EnvVars { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string KeyPath, string ValueName), string> RegistryValues { get; }
            = new();

        public string? GetEnvironmentVariable(string name) =>
            EnvVars.TryGetValue(name, out var v) ? v : null;

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public bool FileExists(string path) => Files.Contains(path);

        public string? GetRegistryValue(string keyPath, string valueName) =>
            RegistryValues.TryGetValue((keyPath, valueName), out var v) ? v : null;
    }
}
