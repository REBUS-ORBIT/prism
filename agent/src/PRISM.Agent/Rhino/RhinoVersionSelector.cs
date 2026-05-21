using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RhinoInside;

namespace PRISM.Agent.Rhino;

/// <summary>
/// Reads <c>RhinoVersion</c> from <see cref="Config.AgentConfig"/> and configures
/// the Rhino.Inside assembly resolver to load Rhino from the matching installation.
///
/// Must be called from <c>Program.Main</c> before any <c>Rhino.*</c> types are accessed.
/// <see cref="Resolver.Initialize(string)"/> hooks <see cref="AppDomain.CurrentDomain.AssemblyResolve"/>
/// so subsequent Rhino assembly loads come from the selected install directory.
///
/// Supported <c>RhinoVersion</c> values:
///   "auto" (default) — calls <see cref="Resolver.Initialize()"/> to select the
///                      highest installed Rhino version via the built-in resolver logic.
///   "8", "9", etc.   — requires that specific major version; probes registry and
///                      standard install paths, throws if not found.
///   anything else    — warning logged, falls back to auto.
/// </summary>
public sealed class RhinoVersionSelector
{
    readonly ILogger<RhinoVersionSelector> _log;

    /// <summary>Path to the selected Rhino System directory, e.g. C:\Program Files\Rhino 8\System</summary>
    public string? SelectedSystemDir { get; private set; }

    /// <summary>True after <see cref="Initialize"/> has successfully called <see cref="Resolver.Initialize"/>.</summary>
    public bool IsInitialized { get; private set; }

    public RhinoVersionSelector(ILogger<RhinoVersionSelector> log) => _log = log;

    /// <summary>
    /// Probe for the requested Rhino version and call
    /// <see cref="Resolver.Initialize(string)"/> (or the parameterless overload for auto).
    /// </summary>
    /// <param name="rhinoVersionConfig">
    /// Value of <c>AgentConfig.RhinoVersion</c>. "auto", a major version integer string
    /// (e.g. "8"), or empty/null to fall back to auto.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a specific major version was requested but is not installed.
    /// </exception>
    public void Initialize(string? rhinoVersionConfig)
    {
        var version = (rhinoVersionConfig ?? "auto").Trim().ToLowerInvariant();

        if (version is "" or "auto")
        {
            // Probe Rhino 8 then 9 ourselves so we always have an explicit path to
            // pass to Resolver.Initialize(systemDir). The no-arg overload registers
            // the AssemblyResolve callback even when it can't find Rhino, leaving
            // RhinoSystemDirectory null — Path.Combine(null, …) then throws on
            // the first assembly load.
            var autoDir = ProbeRhinoSystemDir(8) ?? ProbeRhinoSystemDir(9);

            if (!string.IsNullOrEmpty(autoDir))
            {
                _log.LogInformation("Rhino version selected (auto): {SystemDir}", autoDir);
                SelectedSystemDir = autoDir;
                Resolver.Initialize(autoDir);
                IsInitialized = true;
                return;
            }

            // Last-resort: let Rhino.Inside's own resolver try (it may find Rhino
            // via environment variables or a non-standard install path).
            Resolver.Initialize();
            SelectedSystemDir = Resolver.RhinoSystemDirectory;
            if (!string.IsNullOrEmpty(SelectedSystemDir))
            {
                _log.LogInformation("Rhino version selected (auto/resolver): {SystemDir}", SelectedSystemDir);
                IsInitialized = true;
                return;
            }

            _log.LogWarning(
                "RhinoVersionSelector: no Rhino 8 or 9 installation found. " +
                "The agent will start but jobs will be rejected until Rhino is installed.");
            return;
        }

        if (!int.TryParse(version, out int major))
        {
            _log.LogWarning(
                "RhinoVersionSelector: unrecognised rhinoVersion value \"{Value}\". " +
                "Use \"auto\", \"8\", or \"9\". Falling back to auto.",
                rhinoVersionConfig);
            // Fall through to auto probe (same logic as the "auto" branch above).
            var fallbackDir = ProbeRhinoSystemDir(8) ?? ProbeRhinoSystemDir(9);
            if (!string.IsNullOrEmpty(fallbackDir))
            {
                _log.LogInformation("Rhino version selected (auto fallback): {SystemDir}", fallbackDir);
                SelectedSystemDir = fallbackDir;
                Resolver.Initialize(fallbackDir);
                IsInitialized = true;
            }
            else
            {
                _log.LogWarning("RhinoVersionSelector: no Rhino installation found — agent will start without Rhino");
            }
            return;
        }

        // Specific version requested — probe registry then standard install paths.
        var systemDir = ProbeRhinoSystemDir(major);
        if (string.IsNullOrEmpty(systemDir))
        {
            _log.LogError(
                "RhinoVersionSelector: Rhino {Major} not found at any standard install path. " +
                "Install Rhino {Major} or set \"rhinoVersion\": \"auto\" in agent-config.json.",
                major, major);
            throw new InvalidOperationException(
                $"Rhino {major} is not installed. " +
                $"Install Rhino {major} or change rhinoVersion to \"auto\" in agent-config.json.");
        }

        _log.LogInformation("Rhino version selected: Rhino {Major} at {SystemDir}", major, systemDir);
        SelectedSystemDir = systemDir;
        Resolver.Initialize(systemDir);
        IsInitialized = true;
    }

    /// <summary>
    /// Probe for a specific Rhino major version.
    /// Checks (in order):
    ///   1. HKLM\SOFTWARE\McNeel\Rhinoceros\{major}.0\Install → Path
    ///   2. Standard install path C:\Program Files\Rhino {major}\System
    /// Returns the System directory path, or null if not found.
    /// </summary>
    static string? ProbeRhinoSystemDir(int major)
    {
        // Registry probe (64-bit hive; Rhino is a 64-bit application)
        var regPath = $@"SOFTWARE\McNeel\Rhinoceros\{major}.0\Install";
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                                       .OpenSubKey(regPath);
            if (key?.GetValue("Path") is string installPath && !string.IsNullOrEmpty(installPath))
            {
                var systemDir = Path.Combine(installPath.TrimEnd('\\', '/'), "System");
                if (IsValidRhinoSystemDir(systemDir))
                    return systemDir;
            }
        }
        catch
        {
            // Registry access failed; fall through to path probe.
        }

        // Standard install path fallback
        var standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            $"Rhino {major}", "System");
        if (IsValidRhinoSystemDir(standard))
            return standard;

        return null;
    }

    static bool IsValidRhinoSystemDir(string dir) =>
        Directory.Exists(dir) &&
        File.Exists(Path.Combine(dir, "RhinoCommon.dll"));
}
