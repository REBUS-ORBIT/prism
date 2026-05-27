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
    public static UnrealInstall? TryResolve(IEnvironmentProbe? probe = null) =>
        ResolveDetailed(probe).Install;

    /// <summary>
    /// Like <see cref="TryResolve"/> but also returns a list of per-probe
    /// outcomes. Callers can use the diagnostics to produce an actionable
    /// failure message instead of the opaque "env var is set but invalid"
    /// string that ships in <see cref="UnrealResolution.Install"/>=null
    /// failures. The diagnostic list is always populated, even on the
    /// happy path, so logs can show which candidate matched first and
    /// which paths the resolver inspected.
    /// </summary>
    public static UnrealResolution ResolveDetailed(IEnvironmentProbe? probe = null)
    {
        probe ??= DefaultEnvironmentProbe.Instance;

        var diagnostics = new List<UnrealProbeOutcome>(3);

        // 1. Env var — explicit configuration always wins.
        var rawFromEnv = probe.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(rawFromEnv))
        {
            var outcome = ProbeFromRoot(rawFromEnv, UnrealResolutionSource.EnvironmentVariable, probe);
            diagnostics.Add(outcome);
            if (outcome.Install is not null)
                return new UnrealResolution(outcome.Install, diagnostics);
            // Env var was set but pointed at a missing / invalid root —
            // fall through to other sources but keep the failure so
            // the CLI can include it in the typed "ue_root_not_found" event.
        }

        // 2. Default install path.
        var defaultOutcome = ProbeFromRoot(DefaultInstallRoot, UnrealResolutionSource.DefaultPath, probe);
        diagnostics.Add(defaultOutcome);
        if (defaultOutcome.Install is not null)
            return new UnrealResolution(defaultOutcome.Install, diagnostics);

        // 3. Registry lookup.
        var rawFromRegistry = probe.GetRegistryValue(RegistryKeyPath, RegistryValueName);
        if (!string.IsNullOrWhiteSpace(rawFromRegistry))
        {
            var outcome = ProbeFromRoot(rawFromRegistry, UnrealResolutionSource.Registry, probe);
            diagnostics.Add(outcome);
            if (outcome.Install is not null)
                return new UnrealResolution(outcome.Install, diagnostics);
        }
        else
        {
            // Surface "registry value unset" as its own diagnostic so
            // the failure message can say "HKLM\…\InstalledDirectory not
            // present" instead of silently skipping the probe.
            diagnostics.Add(new UnrealProbeOutcome(
                Source: UnrealResolutionSource.Registry,
                RawRoot: null,
                NormalizedRoot: null,
                DirectoryExists: false,
                ExpectedEditorPath: null,
                EditorExists: false,
                FailureReason: $"HKLM\\{RegistryKeyPath}\\{RegistryValueName} not present"));
        }

        return new UnrealResolution(null, diagnostics);
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
        string root, UnrealResolutionSource source, IEnvironmentProbe? probe = null) =>
            ProbeFromRoot(root, source, probe).Install;

    /// <summary>
    /// Run the same validation as <see cref="TryFromRoot"/> but return a
    /// <see cref="UnrealProbeOutcome"/> describing exactly which step
    /// failed (raw vs normalized root, directory existence, editor
    /// existence). The orchestrator's failure message uses this so an
    /// operator reading the agent log can tell at a glance whether the
    /// configured path was wrong, the directory was missing, or just
    /// the headless editor was absent (e.g. partial UE install).
    /// </summary>
    public static UnrealProbeOutcome ProbeFromRoot(
        string root, UnrealResolutionSource source, IEnvironmentProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        probe ??= DefaultEnvironmentProbe.Instance;

        // Defensive normalization for paths that round-trip through
        // operator-edited JSON, web forms, or environment blocks set by
        // tools that don't strip trailing whitespace. UTF-8 BOMs land at
        // the START of a value when a JSON file is saved with BOM and the
        // value happens to be the first string; zero-width spaces sneak
        // in when paths are copy-pasted from rich-text sources. None of
        // these characters belong in a Windows path — strip them all
        // before any probe so a value like "\uFEFFC:\Program Files\…\"
        // doesn't silently fail Directory.Exists.
        var normalized = NormalizeRoot(root);
        if (string.IsNullOrEmpty(normalized))
        {
            return new UnrealProbeOutcome(
                Source: source,
                RawRoot: root,
                NormalizedRoot: normalized,
                DirectoryExists: false,
                ExpectedEditorPath: null,
                EditorExists: false,
                FailureReason: "normalized path is empty (only whitespace / BOM characters)");
        }

        var editorCmd = Path.Combine(normalized, "Engine", "Binaries", "Win64", "UnrealEditor-Cmd.exe");
        if (!probe.DirectoryExists(normalized))
        {
            return new UnrealProbeOutcome(
                Source: source,
                RawRoot: root,
                NormalizedRoot: normalized,
                DirectoryExists: false,
                ExpectedEditorPath: editorCmd,
                EditorExists: false,
                FailureReason: $"directory does not exist: {normalized}");
        }

        if (!probe.FileExists(editorCmd))
        {
            return new UnrealProbeOutcome(
                Source: source,
                RawRoot: root,
                NormalizedRoot: normalized,
                DirectoryExists: true,
                ExpectedEditorPath: editorCmd,
                EditorExists: false,
                FailureReason: $"UnrealEditor-Cmd.exe missing at {editorCmd} (directory exists but UE binaries are absent — partial install?)");
        }

        return new UnrealProbeOutcome(
            Source: source,
            RawRoot: root,
            NormalizedRoot: normalized,
            DirectoryExists: true,
            ExpectedEditorPath: editorCmd,
            EditorExists: true,
            FailureReason: null,
            Install: new UnrealInstall(Root: normalized, EditorCmdPath: editorCmd, Source: source));
    }

    /// <summary>
    /// Strip BOM / zero-width characters and ASCII whitespace, then trim
    /// trailing directory separators. Returns the canonical absolute
    /// form via <see cref="Path.GetFullPath(string)"/> when possible —
    /// this collapses mixed separators (<c>C:/foo\bar</c>) and resolves
    /// relative paths against the orchestrator's working directory.
    /// Exposed primarily for tests; production code goes through
    /// <see cref="ProbeFromRoot"/>.
    /// </summary>
    public static string NormalizeRoot(string root)
    {
        if (root is null) return string.Empty;

        // Strip LEADING and TRAILING whitespace + invisible unicode chars
        // (BOM, zero-width spaces/joiners). We deliberately do NOT touch
        // interior whitespace — Windows paths contain spaces all over the
        // place ("C:\Program Files\Epic Games\…"), so a blanket filter
        // would mangle the value. These zero-width characters have
        // historically snuck into agent-config.json values via copy-paste
        // from rich-text sources, and into env vars set by tools that
        // don't strip BOM when reading UTF-8 with BOM JSON files.
        var trimmed = root.Trim();
        // Strip leading/trailing invisible chars (Trim only handles
        // char.IsWhiteSpace).
        var start = 0;
        while (start < trimmed.Length && IsInvisible(trimmed[start])) start++;
        var end = trimmed.Length - 1;
        while (end >= start && IsInvisible(trimmed[end])) end--;
        trimmed = trimmed.Substring(start, end - start + 1);
        if (string.IsNullOrEmpty(trimmed)) return string.Empty;

        // Drop any trailing path separators so Path.Combine behaves
        // predictably (Path.Combine(@"C:\foo\", "bar") still works, but
        // tests want to assert on a clean canonical form).
        trimmed = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(trimmed)) return string.Empty;

        // Path.GetFullPath collapses mixed separators and resolves
        // relative paths to absolute. Wrap in a try/catch because
        // GetFullPath throws on illegal characters (NUL, control chars)
        // and we want to surface those as "normalized path is empty"
        // rather than blow up the orchestrator with an unhandled
        // exception during pre-flight.
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
        catch (PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// True for characters that should be stripped from the leading /
    /// trailing edges of a UE root path: BOMs and zero-width joiners /
    /// spaces. <see cref="char.IsWhiteSpace(char)"/> covers ASCII / NBSP
    /// whitespace and is applied by <see cref="string.Trim()"/>
    /// separately, so this helper only handles the truly invisible
    /// formatting marks that <c>Trim()</c> ignores.
    /// </summary>
    static bool IsInvisible(char ch) =>
        ch == '\uFEFF' || ch == '\u200B' || ch == '\u200C' || ch == '\u200D';

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

/// <summary>
/// Per-probe outcome captured by <see cref="UnrealEnvironment.ProbeFromRoot"/>.
/// <see cref="Install"/> is non-null when the probe succeeded; the other
/// fields are populated on both success and failure so the orchestrator
/// can render a meaningful diagnostic message.
/// </summary>
public sealed record UnrealProbeOutcome(
    UnrealResolutionSource Source,
    string? RawRoot,
    string? NormalizedRoot,
    bool DirectoryExists,
    string? ExpectedEditorPath,
    bool EditorExists,
    string? FailureReason,
    UnrealInstall? Install = null);

/// <summary>
/// Composite result of <see cref="UnrealEnvironment.ResolveDetailed"/>:
/// the winning install (or null when every probe missed), plus the full
/// per-probe trace. The trace is always present so happy-path logs can
/// record "default path matched after env-var probe missed".
/// </summary>
public sealed record UnrealResolution(
    UnrealInstall? Install,
    IReadOnlyList<UnrealProbeOutcome> Diagnostics);
