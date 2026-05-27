using System.Numerics;

namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// Tagged-union base for every node the receive pipeline emits. Sealed
/// records + an abstract base let downstream consumers
/// (<c>SceneFlattener</c>, <c>GltfWriter</c>) pattern-match exhaustively
/// without resorting to runtime <c>is</c>-chains.
/// </summary>
public abstract record StagedNode(string SourceObjectId, string SpeckleType)
{
    /// <summary>Discriminator string used in the scene_manifest.json sidecar.</summary>
    public abstract string Kind { get; }
}

/// <summary>A nested ORBIT Collection. Carries layer metadata + children.</summary>
public sealed record StagedCollection(
    string SourceObjectId,
    string SpeckleType,
    string Name,
    string LayerPath,
    IReadOnlyList<StagedNode> Children)
    : StagedNode(SourceObjectId, SpeckleType)
{
    public override string Kind => "collection";
}

/// <summary>
/// A converted Speckle <c>Mesh</c>. Vertices are kept in the source
/// (right-handed Z-up) coordinate system; the coordinate transform is
/// applied once by <see cref="Staging.GltfWriter"/> at write time per
/// the BUILD.md spec.
/// </summary>
public sealed record StagedMesh(
    string SourceObjectId,
    string SpeckleType,
    string LayerPath,
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<int> Indices,
    IReadOnlyList<Vector3>? Normals,
    IReadOnlyList<Vector2>? TexCoords,
    IReadOnlyList<uint>? Colors,
    string? MaterialId)
    : StagedNode(SourceObjectId, SpeckleType)
{
    public override string Kind => "mesh";
}

/// <summary>
/// A converted Speckle <c>RenderMaterial</c>. Texture refs are
/// resolved (i.e. <c>@blob:HASH</c> placeholders are turned into
/// staged file paths) by <see cref="Converters.FromOrbit.MaterialConverter"/>
/// before this record is constructed.
/// </summary>
public sealed record StagedMaterial(
    string SourceObjectId,
    string SpeckleType,
    string Name,
    long Diffuse,
    long Emissive,
    double Opacity,
    double Roughness,
    double Metalness,
    string? DiffuseTexturePath,
    string? BaseColorTexturePath,
    string? EmissiveTexturePath,
    string? NormalTexturePath)
    : StagedNode(SourceObjectId, SpeckleType)
{
    public override string Kind => "material";
}

/// <summary>
/// Catch-all for object types no <c>FromOrbit</c> converter recognises.
/// Logged + serialised to <c>unknown_objects.jsonl</c> next to the
/// staged glTF for offline inspection. Phases J+ will introduce
/// converters that supersede this entry (e.g. MVR lighting).
/// </summary>
public sealed record StagedUnknown(
    string SourceObjectId,
    string SpeckleType,
    string LayerPath,
    string RawJson)
    : StagedNode(SourceObjectId, SpeckleType)
{
    public override string Kind => "unknown";
}
