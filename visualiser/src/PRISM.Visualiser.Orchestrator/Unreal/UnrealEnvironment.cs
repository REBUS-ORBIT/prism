using System.Runtime.Versioning;

#pragma warning disable CA1416 // Microsoft.Win32.Registry only ships on Windows; the
                              // class is already gated with [SupportedOSPlatform("windows")]
                              // and the project is hard-pinned to win-x64, so the
                              // analyzer's "platform compatibility" warning is redundant.
using Microsoft.Win32;
#pragma warning restore CA1416

namespace PRISM.Visualiser.Orchestrator.Unreal;

/// <summary>
/// Locates a UE 5.7 install on the local machine and validates that
/// <c>UnrealEditor-Cmd.exe</c> exists under it. Resolution order
/// (first hit wins):
///
/// <list type="number">
///   <item><description>
///     <c>UNREAL_ENGINE_ROOT</c> environment variable. Lets us pin
///     the engine in CI / dev workflows without touching the
///     registry. If the variable resolves to a directory but the
///     directory is missing or doesn't contain UnrealEditor-Cmd.exe,
///     <see cref="TryResolve"/> returns null — but the caller can
///     distinguish "not configured" from "configured but invalid"
///     via <see cref="EnvVarSet"/>.
///   </description></item>
///   <item><description>
///     Default install path: <c>C:\Program Files\Epic Games\UE_5.7\</c>.
///   </description></item>
///   <item><description>
///     Registry: <c>HKLM\SOFTWARE\EpicGames\Unreal Engine\5.7\InstalledDirectory</c>
///     (the value Epic's launcher writes when installing the engine).
///   </description></item>
/// </list>
///
/// The class is intentionally pure-static — there's no useful per-instance
/// state, and the orchestrator only needs to resolve once at startup.
/// All inputs (env, registry, file system) are virtualised through
/// <see cref="IEnvironmentProbe"/> so tests don't need a real UE install.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UnrealEnvironment
{
    /// <summary>Default install path Epic's launcher uses for UE 5.7.</summary>
    public const string DefaultInstallRoot = @"C:\Program Files\Epic Games\UE_5.7";

    /// <summary>The env var that pins the engine root.</summary>
    public const string EnvVarName = "UNREAL_ENGINE_ROOT";

    /// <summary>HKLM key Epic's launcher writes for UE 5.7 installs.</summary>
    public const string RegistryKeyPath = @"SOFTWARE\EpicGames\Unreal Engine\5.7";

    /// <summary>Registry value name under <see cref="RegistryKeyPath"/>.</summary>
    public const string RegistryValueName = "InstalledDirectory";

    /// <summary>
    /// Try to resolve a usable UE install. Returns <see langword="null"/>
    /// when no candidate produces a valid root containing
    /// <c>Engine\Binaries\Win64\UnrealEditor-Cmd.exe</c>.
    /// </summary>
    public static UnrealInstall? TryResolve(IEnvironmentProbe? probe = null)
    {
        probe ??= DefaultEnvironmentProbe.Instance;

        // 1. Env var — explicit configuration always wins.
        var fromEnv = probe.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var resolved = TryFromRoot(fromEnv, UnrealResolutionSource.EnvironmentVariable, probe);
            if (resolved is not null) return resolved;
            // Env var was set but pointed at a missing / invalid root —
            // fall through to other sources but record the failure so
            // the CLI can emit a typed "ue_root_not_found" event.
        }

        // 2. Default install path.
        var fromDefault = TryFromRoot(DefaultInstallRoot, UnrealResolutionSource.DefaultPath, probe);
        if (fromDefault is not null) return fromDefault;

        // 3. Registry lookup.
        var fromRegistry = probe.GetRegistryValue(RegistryKeyPath, RegistryValueName);
        if (!string.IsNullOrWhiteSpace(fromRegistry))
        {
            var resolved = TryFromRoot(fromRegistry, UnrealResolutionSource.Registry, probe);
            if (resolved is not null) return resolved;
        }

        return null;
    }

    /// <summary>
    /// True when <see cref="EnvVarName"/> has any value at all (even an
    /// invalid one). The CLI uses this to distinguish "the user
    /// explicitly pointed us at a UE root" (→ exit 4 ue_root_not_found
    /// when invalid) from "we fell off the end of every probe" (→
    /// generic failure).
    /// </summary>
    public static bool EnvVarSet(IEnvironmentProbe? probe = null)
    {
        probe ??= DefaultEnvironmentProbe.Instance;
        var value = probe.GetEnvironmentVariable(EnvVarName);
        return !string.IsNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Validate a candidate root: directory must exist AND contain
    /// <c>Engine\Binaries\Win64\UnrealEditor-Cmd.exe</c>. Used by
    /// <see cref="TryResolve"/> and by tests that want to assert
    /// the validation logic directly.
    /// </summary>
    public static UnrealInstall? TryFromRoot(
        string root, UnrealResolutionSource source, IEnvironmentProbe? probe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        probe ??= DefaultEnvironmentProbe.Instance;

        var trimmed = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!probe.DirectoryExists(trimmed)) return null;

        var editorCmd = Path.Combine(trimmed, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        if (!probe.FileExists(editorCmd)) return null;

        return new UnrealInstall(Root: trimmed, EditorCmdPath: editorCmd, Source: source);
    }

    /// <summary>
    /// Inputs an <see cref="UnrealEnvironment"/> probe needs. Tests
    /// inject a stub; production uses
    /// <see cref="DefaultEnvironmentProbe"/> which delegates to the
    /// real <see cref="System.Environment"/> + <see cref="File"/> +
    /// <see cref="Microsoft.Win32.Registry"/> APIs.
    /// </summary>
    public interface IEnvironmentProbe
    {
        string? GetEnvironmentVariable(string name);
        bool DirectoryExists(string path);
        bool FileExists(string path);
        string? GetRegistryValue(string keyPath, string valueName);
    }

    /// <summary>
    /// Production-mode probe: env vars from <see cref="Environment"/>,
    /// directory / file checks from <see cref="Directory"/> /
    /// <see cref="File"/>, registry from
    /// <see cref="Microsoft.Win32.Registry.LocalMachine"/>.
    /// </summary>
    public sealed class DefaultEnvironmentProbe : IEnvironmentProbe
    {
        public static DefaultEnvironmentProbe Instance { get; } = new();

        public string? GetEnvironmentVariable(string name) =>
            Environment.GetEnvironmentVariable(name);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);

        public string? GetRegistryValue(string keyPath, string valueName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: false);
                return key?.GetValue(valueName) as string;
            }
            catch (Exception ex) when (
                ex is System.Security.SecurityException
                    or UnauthorizedAccessException
                    or IOException)
            {
                // The registry hive can be sandboxed (CI containers,
                // restricted user accounts). Treat as "not present"
                // rather than failing the whole resolution.
                return null;
            }
        }
    }
}

/// <summary>How <see cref="UnrealEnvironment.TryResolve"/> located a UE install.</summary>
public enum UnrealResolutionSource
{
    EnvironmentVariable,
    DefaultPath,
    Registry,
}

/// <summary>
/// Result of <see cref="UnrealEnvironment.TryResolve"/>. Surfaces the
/// resolved root, the absolute path to <c>UnrealEditor-Cmd.exe</c>, and
/// which probe found it (logged for observability).
/// </summary>
public sealed record UnrealInstall(
    string Root,
    string EditorCmdPath,
    UnrealResolutionSource Source);
