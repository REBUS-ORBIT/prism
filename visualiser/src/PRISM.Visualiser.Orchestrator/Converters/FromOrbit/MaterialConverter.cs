using System.Text.Json.Nodes;

using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Converters.FromOrbit;

/// <summary>
/// Converts <c>Objects.Other.RenderMaterial</c> into a
/// <see cref="StagedMaterial"/>.
///
/// Texture references on the wire are encoded as <c>@blob:HASH</c>
/// placeholders (the Speckle convention). The receive pipeline runs
/// a blob-resolution pre-pass that turns each placeholder into a
/// path under <c>cache/blobs/</c> + <c>stage/{runId}/textures/</c>;
/// this converter looks the resolved path up in
/// <see cref="ConversionContext.BlobPaths"/>.
///
/// Missing blob (the pre-pass couldn't fetch one) → null path on the
/// staged material. The glTF writer drops the texture channel in
/// that case rather than embedding a broken URI; a Serilog warning
/// is emitted so the issue surfaces in the run logs.
/// </summary>
public sealed class MaterialConverter : IFromOrbitConverter
{
    public const string OrbitTypeName = "Objects.Other.RenderMaterial";

    /// <summary>Texture-ref placeholder prefix on the wire.</summary>
    public const string BlobRefPrefix = "@blob:";

    public bool CanConvert(OrbitObject obj) =>
        obj.SpeckleType == OrbitTypeName;

    public StagedNode Convert(OrbitObject obj, ConversionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(ctx);

        return ConvertCore(obj.Id, obj.SpeckleType, obj.Raw, ctx);
    }

    /// <summary>
    /// Convert an inline material body (a JSON object embedded under
    /// <c>renderMaterial</c> on a mesh). Reuses the same logic as the
    /// detached-material path but stamps a synthetic id so the
    /// StagedMaterial registry stays unique.
    /// </summary>
    public StagedMaterial ConvertInline(string syntheticId, JsonObject body, ConversionContext ctx)
    {
        ArgumentException.ThrowIfNullOrEmpty(syntheticId);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(ctx);
        var node = ConvertCore(syntheticId, OrbitTypeName, body, ctx);
        return (StagedMaterial)node;
    }

    private StagedMaterial ConvertCore(
        string id, string speckleType, JsonObject raw, ConversionContext ctx)
    {
        var name = raw["name"]?.GetValue<string>() ?? "default";
        var diffuse = raw["diffuse"]?.GetValue<long>() ?? 0xFFFFFFFFL;
        var emissive = raw["emissive"]?.GetValue<long>() ?? 0xFF000000L;
        var opacity = raw["opacity"]?.GetValue<double>() ?? 1.0;
        var roughness = raw["roughness"]?.GetValue<double>() ?? 0.5;
        var metalness = raw["metalness"]?.GetValue<double>() ?? 0.0;

        return new StagedMaterial(
            SourceObjectId: id,
            SpeckleType: speckleType,
            Name: name,
            Diffuse: diffuse,
            Emissive: emissive,
            Opacity: opacity,
            Roughness: roughness,
            Metalness: metalness,
            DiffuseTexturePath: ResolveTexture(raw, "diffuseTexture", ctx),
            BaseColorTexturePath: ResolveTexture(raw, "baseColorTexture", ctx),
            EmissiveTexturePath: ResolveTexture(raw, "emissiveTexture", ctx),
            NormalTexturePath: ResolveTexture(raw, "normalTexture", ctx));
    }

    private static string? ResolveTexture(JsonObject raw, string field, ConversionContext ctx)
    {
        var refValue = raw[field]?.GetValue<string>();
        if (string.IsNullOrEmpty(refValue)) return null;

        // Two encodings co-exist on the wire:
        //   1. "@blob:HASH" — placeholder produced by the connector
        //      send path before it knows the server-assigned blob id.
        //   2. plain "HASH" — server-assigned id post-upload.
        //
        // Both resolve through BlobPaths the same way (the pipeline's
        // pre-pass uses the same hash for both), so strip the prefix
        // when present and look the bare hash up.
        var hash = refValue.StartsWith(BlobRefPrefix, StringComparison.Ordinal)
            ? refValue[BlobRefPrefix.Length..]
            : refValue;
        if (string.IsNullOrEmpty(hash)) return null;

        if (ctx.BlobPaths.TryGetValue(hash, out var path))
        {
            return path;
        }

        ctx.Logger.Warning(
            "material texture blob unresolved field={Field} hash={Hash}", field, hash);
        return null;
    }

    /// <summary>
    /// Yield every <c>@blob:HASH</c> reference inside a RenderMaterial
    /// body, returning the bare hash strings. Used by the receive
    /// pipeline's blob-resolution pre-pass.
    /// </summary>
    public static IEnumerable<string> EnumerateBlobHashes(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        foreach (var field in TextureFields)
        {
            var v = body[field]?.GetValue<string>();
            if (string.IsNullOrEmpty(v)) continue;
            var hash = v.StartsWith(BlobRefPrefix, StringComparison.Ordinal)
                ? v[BlobRefPrefix.Length..]
                : v;
            if (!string.IsNullOrEmpty(hash)) yield return hash;
        }
    }

    /// <summary>Texture-bearing field names per the SDK's RenderMaterial.</summary>
    public static readonly string[] TextureFields = new[]
    {
        "diffuseTexture", "baseColorTexture",
        "emissiveTexture", "pbrEmissionTexture",
        "roughnessTexture", "metalnessTexture",
        "normalTexture", "opacityTexture",
    };
}
