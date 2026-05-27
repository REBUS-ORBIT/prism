using System.Text.Json.Nodes;

namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// Loose ORBIT/Speckle "Base" object. Phase C deliberately does NOT
/// strongly type the wire format because:
///
///   1. Speckle objects carry arbitrary user properties the orchestrator
///      must round-trip without loss for the agent / UE side.
///   2. The schema evolves out-of-band with the orchestrator's release
///      cadence — adding a new geometry primitive on the server should
///      not require a visualiser bump.
///
/// Strong types live in <see cref="Converters.FromOrbit"/>; everything
/// else stays in <see cref="Raw"/> as a <see cref="JsonObject"/>. The
/// receive pipeline walks <see cref="EnumerateReferenceIds"/> to resolve
/// detached children via <see cref="OrbitApi.IOrbitApi"/>.
/// </summary>
public sealed class OrbitObject
{
    /// <summary>
    /// Server-assigned content hash. Empty when an object is constructed
    /// in tests without serialising back through the transport layer.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Speckle/ORBIT discriminator (e.g. <c>"Objects.Geometry.Mesh"</c>).
    /// Converters dispatch on the <em>full</em> dotted form: prefix
    /// matching against <c>"Objects.Geometry."</c> would pick up a
    /// future <c>Objects.Geometry.Brep</c> as a <see cref="MeshConverter"/>
    /// candidate, which is wrong.
    /// </summary>
    public string SpeckleType { get; }

    public string? Name { get; }

    public JsonObject Raw { get; }

    public OrbitObject(string id, string speckleType, string? name, JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        Id = id ?? string.Empty;
        SpeckleType = speckleType ?? string.Empty;
        Name = name;
        Raw = raw;
    }

    /// <summary>
    /// Parse a JSON document into a loose <see cref="OrbitObject"/>.
    /// Tolerates both <c>id</c> and <c>_id</c>, and both
    /// <c>speckle_type</c> and <c>type</c> as the discriminator field —
    /// historical payloads in the ORBIT data lake use either.
    /// </summary>
    public static OrbitObject Parse(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        if (JsonNode.Parse(json) is not JsonObject node)
        {
            throw new InvalidDataException(
                "ORBIT object JSON must deserialise to a JSON object.");
        }
        return From(node);
    }

    public static OrbitObject From(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var id = TryGetString(node, "id")
              ?? TryGetString(node, "_id")
              ?? string.Empty;
        var speckleType = TryGetString(node, "speckle_type")
                       ?? TryGetString(node, "type")
                       ?? string.Empty;
        var name = TryGetString(node, "name");
        return new OrbitObject(id, speckleType, name, node);
    }

    private static string? TryGetString(JsonObject node, string key)
    {
        if (!node.TryGetPropertyValue(key, out var v) || v is null) return null;
        return v.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? v.GetValue<string>()
            : null;
    }

    /// <summary>
    /// Yield every detached-child <c>referencedId</c> reachable from
    /// this object. Speckle's wire format replaces detached children
    /// with <c>{referencedId: "...", speckle_type: "reference"}</c>
    /// stubs; the receive pipeline turns each yielded id into another
    /// <c>GET /api/v1/objects/{id}</c> fetch.
    /// </summary>
    public IEnumerable<string> EnumerateReferenceIds()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refId in WalkRefs(Raw))
        {
            if (seen.Add(refId)) yield return refId;
        }
    }

    private static IEnumerable<string> WalkRefs(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue("referencedId", out var refIdNode)
                    && refIdNode is JsonValue v
                    && v.TryGetValue<string>(out var refId)
                    && !string.IsNullOrEmpty(refId))
                {
                    yield return refId;
                    yield break;
                }
                foreach (var kv in obj)
                {
                    foreach (var r in WalkRefs(kv.Value))
                        yield return r;
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    foreach (var r in WalkRefs(item))
                        yield return r;
                }
                break;
        }
    }
}
