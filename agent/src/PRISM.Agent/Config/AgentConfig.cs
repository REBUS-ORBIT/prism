using System.IO;
using System.Text.Json;
using PRISM.Contracts;

namespace PRISM.Agent.Config;

public sealed record AgentConfig
{
    public string PrismUrl   { get; init; } = "wss://prism.rebus.industries/ws/agent";
    public string NodeName   { get; init; } = Environment.MachineName;
    public string MachineId  { get; init; } = "auto";
    public int    Slots      { get; init; } = 1;
    public AgentRole[] Roles { get; init; } = new[] { AgentRole.Conversion, AgentRole.Layering, AgentRole.Receive };
    public string? RhinoExecutablePath { get; init; }

    /// <summary>
    /// Which Rhino version to host. Values:
    ///   "auto" (default) — probe for the highest installed version
    ///   "8"             — require Rhino 8 specifically
    ///   "9"             — require Rhino 9 specifically (future; fails fast if not installed)
    /// </summary>
    public string RhinoVersion { get; init; } = "auto";

    public string  LogDir { get; init; } = @"C:\ProgramData\PRISM.Agent\logs";

    public static AgentConfig Load(string? path = null)
    {
        path ??= ResolveDefaultPath();

        AgentConfig raw;
        if (!File.Exists(path))
        {
            raw = new AgentConfig();
        }
        else
        {
            var json = File.ReadAllText(path);
            raw = JsonSerializer.Deserialize<AgentConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }) ?? throw new InvalidOperationException($"failed to parse {path}");
        }

        // Always resolve machineId so the agent never sends the literal string "auto".
        return raw with
        {
            MachineId = ResolveMachineId(raw.MachineId),
        };
    }

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
        var path = Path.Combine(dir, "machine-id");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        var id = Guid.NewGuid().ToString();
        File.WriteAllText(path, id);
        return id;
    }
}
