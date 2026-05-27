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
    public void EnvVar_WithTrailingBackslash_Resolves()
    {
        // Repro for ue_root_not_found on PC01: agent-config.json shipped
        // `"C:\\Program Files\\Epic Games\\UE_5.7\\"` (trailing
        // backslash). Before NormalizeRoot landed, that value made
        // Directory.Exists fail when the probe's FakeProbe used
        // case-insensitive but exact-character matching. Strip the
        // trailing separator + canonicalize so the Editor probe lines
        // up with the registered file set.
        const string rawRoot = @"C:\Custom\UE_5.7\";
        const string canonicalRoot = @"C:\Custom\UE_5.7";
        var probe = new FakeProbe
        {
            EnvVars = { [UnrealEnvironment.EnvVarName] = rawRoot },
            Directories = { canonicalRoot },
            Files = { System.IO.Path.Combine(canonicalRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe") },
        };

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.NotNull(install);
        Assert.Equal(canonicalRoot, install!.Root);
        Assert.Equal(UnrealResolutionSource.EnvironmentVariable, install.Source);
    }

    [Fact]
    public void EnvVar_WithLeadingBom_Resolves()
    {
        // UTF-8 BOM (U+FEFF) at the start of a string value is what
        // sneaks in when a Windows editor saves agent-config.json with
        // BOM and the JSON parser then reads the first scalar. Strip it
        // before any directory probe so a BOM-prefixed root doesn't
        // silently fail.
        const string canonicalRoot = @"C:\BomTest\UE_5.7";
        var rawRoot = "\uFEFF" + canonicalRoot;
        var probe = new FakeProbe
        {
            EnvVars = { [UnrealEnvironment.EnvVarName] = rawRoot },
            Directories = { canonicalRoot },
            Files = { System.IO.Path.Combine(canonicalRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe") },
        };

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.NotNull(install);
        Assert.Equal(canonicalRoot, install!.Root);
    }

    [Fact]
    public void EnvVar_WithMixedSeparators_ResolvesToCanonicalForm()
    {
        // Operators occasionally enter forward-slash paths in the
        // SettingsForm (muscle memory from POSIX shells). Path.GetFullPath
        // collapses these to backslashes on Windows.
        const string canonicalRoot = @"C:\MixedSep\UE_5.7";
        const string rawRoot = "C:/MixedSep/UE_5.7";
        var probe = new FakeProbe
        {
            EnvVars = { [UnrealEnvironment.EnvVarName] = rawRoot },
            Directories = { canonicalRoot },
            Files = { System.IO.Path.Combine(canonicalRoot, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe") },
        };

        var install = UnrealEnvironment.TryResolve(probe);

        Assert.NotNull(install);
        Assert.Equal(canonicalRoot, install!.Root);
    }

    [Fact]
    public void ResolveDetailed_OnFailure_PopulatesPerProbeDiagnostics()
    {
        const string ghostRoot = @"C:\does-not-exist\UE_5.7";
        var probe = new FakeProbe
        {
            EnvVars = { [UnrealEnvironment.EnvVarName] = ghostRoot },
            // No directories / files registered → every probe misses.
        };

        var resolution = UnrealEnvironment.ResolveDetailed(probe);

        Assert.Null(resolution.Install);
        Assert.Equal(3, resolution.Diagnostics.Count); // env, default, registry
        var envProbe = resolution.Diagnostics[0];
        Assert.Equal(UnrealResolutionSource.EnvironmentVariable, envProbe.Source);
        Assert.Equal(ghostRoot, envProbe.RawRoot);
        Assert.Equal(ghostRoot, envProbe.NormalizedRoot);
        Assert.False(envProbe.DirectoryExists);
        Assert.NotNull(envProbe.FailureReason);
        Assert.Contains("does not exist", envProbe.FailureReason!);
    }

    [Fact]
    public void ResolveDetailed_OnDirExistsButEditorMissing_ReportsPartialInstall()
    {
        const string root = @"C:\PartialInstall\UE_5.7";
        var probe = new FakeProbe
        {
            EnvVars = { [UnrealEnvironment.EnvVarName] = root },
            Directories = { root },
            // Files set intentionally empty — UnrealEditor-Cmd.exe
            // missing simulates a partial / cancelled UE install.
        };

        var resolution = UnrealEnvironment.ResolveDetailed(probe);

        Assert.Null(resolution.Install);
        var envProbe = resolution.Diagnostics[0];
        Assert.True(envProbe.DirectoryExists);
        Assert.False(envProbe.EditorExists);
        Assert.NotNull(envProbe.FailureReason);
        Assert.Contains("UnrealEditor-Cmd.exe missing", envProbe.FailureReason!);
        Assert.Contains("partial install", envProbe.FailureReason!);
    }

    [Fact]
    public void NormalizeRoot_StripsBomAndTrailingSlash()
    {
        var result = UnrealEnvironment.NormalizeRoot("\uFEFFC:\\Foo\\Bar\\");
        Assert.Equal(@"C:\Foo\Bar", result);
    }

    [Fact]
    public void NormalizeRoot_StripsWhitespace()
    {
        var result = UnrealEnvironment.NormalizeRoot("   C:\\Foo\\Bar   ");
        Assert.Equal(@"C:\Foo\Bar", result);
    }

    [Fact]
    public void NormalizeRoot_PreservesInteriorWhitespace()
    {
        // Windows paths legitimately contain spaces ("C:\Program Files").
        // The interior whitespace MUST be preserved.
        var result = UnrealEnvironment.NormalizeRoot(@"C:\Program Files\Epic Games\UE_5.7\");
        Assert.Equal(@"C:\Program Files\Epic Games\UE_5.7", result);
    }

    [Fact]
    public void NormalizeRoot_EmptyOrNullReturnsEmpty()
    {
        Assert.Equal(string.Empty, UnrealEnvironment.NormalizeRoot(null!));
        Assert.Equal(string.Empty, UnrealEnvironment.NormalizeRoot(string.Empty));
        Assert.Equal(string.Empty, UnrealEnvironment.NormalizeRoot("   "));
        Assert.Equal(string.Empty, UnrealEnvironment.NormalizeRoot("\uFEFF\u200B\u200C"));
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
