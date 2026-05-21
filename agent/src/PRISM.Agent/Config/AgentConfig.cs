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
        path ??= ResolveDefaultPath();

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
    // Save — writes the current state back to disk
    // -------------------------------------------------------------------------
    public void Save(string? path = null)
    {
        var savePath = path ?? LoadedPath ?? ResolveDefaultPath();
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, _writeOpts);
        File.WriteAllText(savePath, json, Encoding.UTF8);
        LoadedPath = savePath;
    }

    // -------------------------------------------------------------------------
    // Path resolution
    // -------------------------------------------------------------------------
    static string ResolveDefaultPath()
    {
        // Prefer the file next to the .exe; fall back to ProgramData.
        var exeDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(exeDir, "agent-config.json");
        if (File.Exists(candidate)) return candidate;
        return Path.Combine(@"C:\ProgramData\PRISM.Agent", "agent-config.json");
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
