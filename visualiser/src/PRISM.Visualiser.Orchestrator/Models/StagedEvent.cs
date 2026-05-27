using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// Intermediate stdout event the orchestrator emits after the receive
/// pipeline has staged a glTF + manifest but BEFORE Phase D/E hand
/// the staged bundle to UE. The agent uses this to know "the
/// orchestrator made it as far as a valid scene on disk" even when
/// the eventual ready event won't be reached because the run exits
/// with <see cref="Program.ExitCodes.NotImplemented"/>.
///
/// Wire schema: <c>prism-visualiser/staged/v1</c>.
/// </summary>
public sealed record StagedEvent(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("stagePath")] string StagePath,
    [property: JsonPropertyName("manifestPath")] string ManifestPath,
    [property: JsonPropertyName("gltfPath")] string GltfPath,
    [property: JsonPropertyName("objectCount")] int ObjectCount,
    [property: JsonPropertyName("meshCount")] int MeshCount,
    [property: JsonPropertyName("materialCount")] int MaterialCount,
    [property: JsonPropertyName("textureCount")] int TextureCount,
    [property: JsonPropertyName("unknownCount")] int UnknownCount)
{
    public const string SchemaName = "prism-visualiser/staged/v1";

    public static StagedEvent For(
        string runId,
        string stagePath,
        string manifestPath,
        string gltfPath,
        int objectCount,
        int meshCount,
        int materialCount,
        int textureCount,
        int unknownCount) =>
        new(
            Schema: SchemaName,
            RunId: runId,
            StagePath: stagePath,
            ManifestPath: manifestPath,
            GltfPath: gltfPath,
            ObjectCount: objectCount,
            MeshCount: meshCount,
            MaterialCount: materialCount,
            TextureCount: textureCount,
            UnknownCount: unknownCount);

    public string ToJsonLine() =>
        JsonSerializer.Serialize(this, StagedEventJsonContext.Default.StagedEvent);
}

[JsonSerializable(typeof(StagedEvent))]
internal sealed partial class StagedEventJsonContext : JsonSerializerContext { }
