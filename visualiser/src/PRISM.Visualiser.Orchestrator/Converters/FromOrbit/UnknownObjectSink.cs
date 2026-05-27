using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRISM.Visualiser.Orchestrator.Converters.FromOrbit;

/// <summary>
/// Append-only JSONL writer that records every ORBIT object the
/// FromOrbit converter set didn't recognise. The file lives next to
/// the staged glTF so an offline triage script can scan it without
/// re-running the pipeline; the in-memory mirror lets tests assert
/// on what got logged without touching disk.
///
/// Format: one JSON object per line, schema-stamped:
/// <code>
/// {"schema":"prism-visualiser/unknown-object/v1","objectId":"...","speckleType":"...","layerPath":"...","loggedAt":"..."}
/// </code>
/// </summary>
public sealed class UnknownObjectSink
{
    public const string SchemaName = "prism-visualiser/unknown-object/v1";

    private readonly string? _filePath;
    private readonly List<UnknownEntry> _entries = new();
    private readonly object _gate = new();

    /// <summary>In-memory only sink — useful for tests.</summary>
    public UnknownObjectSink() { }

    /// <summary>Sink that mirrors writes to <paramref name="filePath"/>.</summary>
    public UnknownObjectSink(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public IReadOnlyList<UnknownEntry> Entries
    {
        get { lock (_gate) { return _entries.ToArray(); } }
    }

    public void Record(string objectId, string speckleType, string layerPath)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(speckleType);
        ArgumentNullException.ThrowIfNull(layerPath);

        var entry = new UnknownEntry(
            Schema: SchemaName,
            ObjectId: objectId,
            SpeckleType: speckleType,
            LayerPath: layerPath,
            LoggedAt: DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _entries.Add(entry);
            if (_filePath is not null)
            {
                var line = JsonSerializer.Serialize(entry, UnknownSinkContext.Default.UnknownEntry);
                File.AppendAllText(_filePath, line + "\n", Encoding.UTF8);
            }
        }
    }
}

public sealed record UnknownEntry(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("objectId")] string ObjectId,
    [property: JsonPropertyName("speckleType")] string SpeckleType,
    [property: JsonPropertyName("layerPath")] string LayerPath,
    [property: JsonPropertyName("loggedAt")] DateTimeOffset LoggedAt);

[JsonSerializable(typeof(UnknownEntry))]
internal sealed partial class UnknownSinkContext : JsonSerializerContext { }
