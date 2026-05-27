using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// Phase E stdout event emitted after the per-run UE project has been
/// imported and the level saved. Sits between the Phase C
/// <c>prism-visualiser/staged/v1</c> event and the eventual Phase F
/// <c>prism-visualiser/ready/v1</c> event.
///
/// Wire schema: <c>prism-visualiser/imported/v1</c>.
/// </summary>
public sealed record ImportedEvent(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("levelPath")] string LevelPath,
    [property: JsonPropertyName("importDurationMs")] long ImportDurationMs,
    [property: JsonPropertyName("assetCount")] int AssetCount)
{
    public const string SchemaName = "prism-visualiser/imported/v1";

    public static ImportedEvent For(
        string runId,
        string projectPath,
        string levelPath,
        long importDurationMs,
        int assetCount) =>
        new(
            Schema: SchemaName,
            RunId: runId,
            ProjectPath: projectPath,
            LevelPath: levelPath,
            ImportDurationMs: importDurationMs,
            AssetCount: assetCount);

    public string ToJsonLine() =>
        JsonSerializer.Serialize(this, ImportedEventJsonContext.Default.ImportedEvent);
}

[JsonSerializable(typeof(ImportedEvent))]
internal sealed partial class ImportedEventJsonContext : JsonSerializerContext { }
