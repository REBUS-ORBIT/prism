using System.Text.Json;
using System.Text.Json.Serialization;

namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// Phase E stdout event for typed failure reporting. The agent watches
/// for either <c>prism-visualiser/imported/v1</c> (success) or
/// <c>prism-visualiser/failed/v1</c> (failure) on stdout to decide what
/// to surface back to the PRISM server.
///
/// <para>
/// Established failure codes:
/// <list type="table">
///   <item><term><c>ue_root_not_found</c></term>
///         <description>UE 5.7 install couldn't be located.</description></item>
///   <item><term><c>template_not_found</c></term>
///         <description>Template release asset returned 404.</description></item>
///   <item><term><c>template_fetch_failed</c></term>
///         <description>Other download failures (network, 5xx, integrity).</description></item>
///   <item><term><c>scaffold_failed</c></term>
///         <description>Per-run project scaffolding failed.</description></item>
///   <item><term><c>ue_timeout</c></term>
///         <description>UE didn't emit a ready marker within the budget.</description></item>
///   <item><term><c>ue_import_failed</c></term>
///         <description>Python emitted an error marker, or UE exited non-zero.</description></item>
///   <item><term><c>signalling_not_found</c></term>
///         <description>Cirrus script isn't installed under the UE root (PixelStreaming2 plugin missing).</description></item>
///   <item><term><c>node_not_found</c></term>
///         <description>UE's bundled <c>node.exe</c> doesn't exist (partial install).</description></item>
///   <item><term><c>signalling_start_timeout</c></term>
///         <description>Cirrus didn't log the ready line within 30s.</description></item>
///   <item><term><c>ue_game_start_timeout</c></term>
///         <description>UE -game mode didn't register a streamer with Cirrus within 120s.</description></item>
///   <item><term><c>ue_game_crashed</c></term>
///         <description>UE exited non-zero before the streamer connected.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed record FailedEvent(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message)
{
    public const string SchemaName = "prism-visualiser/failed/v1";

    public const string CodeUeRootNotFound = "ue_root_not_found";
    public const string CodeTemplateNotFound = "template_not_found";
    public const string CodeTemplateFetchFailed = "template_fetch_failed";
    public const string CodeScaffoldFailed = "scaffold_failed";
    public const string CodeUeTimeout = "ue_timeout";
    public const string CodeUeImportFailed = "ue_import_failed";

    // Phase F — Pixel Streaming 2 bring-up.
    public const string CodeSignallingNotFound = "signalling_not_found";
    public const string CodeNodeNotFound = "node_not_found";
    public const string CodeSignallingStartTimeout = "signalling_start_timeout";
    public const string CodeUeGameStartTimeout = "ue_game_start_timeout";
    public const string CodeUeGameCrashed = "ue_game_crashed";

    public static FailedEvent For(string runId, string code, string message) =>
        new(Schema: SchemaName, RunId: runId, Code: code, Message: message);

    public string ToJsonLine() =>
        JsonSerializer.Serialize(this, FailedEventJsonContext.Default.FailedEvent);
}

[JsonSerializable(typeof(FailedEvent))]
internal sealed partial class FailedEventJsonContext : JsonSerializerContext { }
