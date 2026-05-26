using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PRISM.Contracts;

namespace PRISM.Agent.Config;

public sealed class AgentConfig
{
    public string PrismUrl   { get; set; } = "wss://prism.rebus.industries/ws/agent";
    public string NodeName   { get; set; } = Environment.MachineName;
    public string MachineId  { get; set; } = "auto";
    public int    Slots      { get; set; } = 1;
    public AgentRole[] Roles { get; set; } = new[] { AgentRole.Conversion, AgentRole.Layering, AgentRole.Receive };
    public string? RhinoExecutablePath { get; set; }

    /// <summary>
    /// Which Rhino version to host. Values:
    ///   "auto" (default) — probe for the highest installed version
    ///   "8"             — require Rhino 8 specifically
    ///   "9"             — require Rhino 9 specifically (future; fails fast if not installed)
    /// </summary>
    public string RhinoVersion { get; set; } = "auto";

    public string LogDir { get; set; } = @"C:\ProgramData\PRISM.Agent\logs";

    /// <summary>
    /// Port the agent's local web UI binds to.  The UI is a single page served
    /// straight from the agent process for in-place configuration -- see
    /// <c>WebUi/AgentWebUi.cs</c>.  Defaults to 7421.  Set to 0 to disable
    /// the web UI entirely (the tray menu still works).
    /// </summary>
    public int WebUiPort { get; set; } = 7421;

    /// <summary>
    /// When true (default) the web UI binds to <c>0.0.0.0</c>
    /// (Windows: <c>http://+:port/</c>) so operators can reach a workstation's
    /// settings page from any other machine on the LAN.  The Inno installer
    /// pre-registers a URL ACL for the configured port so this works without
    /// the agent process being elevated.
    ///
    /// Set to false to bind to <c>localhost</c> only -- the page is then
    /// reachable from the workstation itself but not from the LAN.
    ///
    /// Note: the local UI is unauthenticated.  Only leave LAN binding on
    /// when the agent is running on a trusted network segment.
    /// </summary>
    public bool WebUiBindAll { get; set; } = true;

    /// <summary>
    /// Path the config was loaded from (or last saved to). Not persisted to JSON.
    /// </summary>
    [JsonIgnore]
    public string? LoadedPath { get; private set; }

    // -------------------------------------------------------------------------
    // Serializer options shared across Load / Save
    // -------------------------------------------------------------------------
    static readonly JsonSerializerOptions _readOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    static readonly JsonSerializerOptions _writeOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // -------------------------------------------------------------------------
    // Load
    // -------------------------------------------------------------------------
    public static AgentConfig Load(string? path = null)
    {
        path ??= ResolveLoadPath();

        AgentConfig cfg;
        if (!File.Exists(path))
        {
            cfg = new AgentConfig();
        }
        else
        {
            var json = File.ReadAllText(path);
            cfg = JsonSerializer.Deserialize<AgentConfig>(json, _readOpts)
                  ?? throw new InvalidOperationException($"failed to parse {path}");
        }

        cfg.LoadedPath = path;
        cfg.MachineId  = ResolveMachineId(cfg.MachineId);
        return cfg;
    }

    // -------------------------------------------------------------------------
    // Save -- always targets ProgramData so a non-elevated agent (the common
    // case when the scheduled task runs as the interactive workstation user)
    // can persist setting changes.  Program Files is read-only for non-admin
    // users, so the legacy "save next to the EXE" behaviour broke the web
    // UI's Save button with a 500 ACL error on workstations whose login user
    // is not a local administrator.
    // -------------------------------------------------------------------------
    public void Save(string? path = null)
    {
        var savePath = path ?? ProgramDataConfigPath;
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, _writeOpts);

        try
        {
            File.WriteAllText(savePath, json, Encoding.UTF8);
        }
        catch (UnauthorizedAccessException) when (path is null)
        {
            // Last-ditch fallback to %LOCALAPPDATA% so the agent never silently
            // throws away an operator's config edit, even on locked-down boxes
            // where ProgramData is restricted.
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PRISM.Agent", "agent-config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            File.WriteAllText(local, json, Encoding.UTF8);
            savePath = local;
        }

        LoadedPath = savePath;

        // Best-effort cleanup: if the previous on-disk config lived next to
        // the EXE (legacy v0.1.x layout), remove it so subsequent loads
        // don't see a stale Program Files copy.
        try
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "agent-config.json");
            if (!string.Equals(legacy, savePath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(legacy))
            {
                File.Delete(legacy);
            }
        }
        catch
        {
            // Non-fatal; the next Save() will retry, and Load() prefers
            // ProgramData anyway so the legacy file is harmless if left.
        }
    }

    // -------------------------------------------------------------------------
    // Path resolution
    // -------------------------------------------------------------------------
    static string ProgramDataConfigPath =>
        Path.Combine(@"C:\ProgramData\PRISM.Agent", "agent-config.json");

    /// <summary>
    /// Pick the on-disk config to load.  ProgramData wins because Save() now
    /// targets it; the EXE-adjacent legacy path is checked only as a fallback
    /// for v0.1.x installs that wrote their initial config to Program Files.
    /// </summary>
    static string ResolveLoadPath()
    {
        var programData = ProgramDataConfigPath;
        if (File.Exists(programData)) return programData;

        var legacy = Path.Combine(AppContext.BaseDirectory, "agent-config.json");
        if (File.Exists(legacy)) return legacy;

        // No config yet -- caller will create one with defaults and the next
        // Save() lands in ProgramData.
        return programData;
    }

    static string ResolveMachineId(string raw)
    {
        if (!string.Equals(raw, "auto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(raw))
            return raw;

        // Persist a stable GUID in ProgramData so re-installs don't churn workstation rows.
        var dir = @"C:\ProgramData\PRISM.Agent";
        Directory.CreateDirectory(dir);
        var idPath = Path.Combine(dir, "machine-id");
        if (File.Exists(idPath))
        {
            var existing = File.ReadAllText(idPath).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(idPath, id);
        return id;
    }
}
