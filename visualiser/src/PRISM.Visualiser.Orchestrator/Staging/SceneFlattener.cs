using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Staging;

/// <summary>
/// Flattens a <see cref="StagedScene"/> tree into the linear lists the
/// glTF writer consumes:
/// <list type="bullet">
///   <item><description>One <see cref="FlatMesh"/> per <see cref="StagedMesh"/>
///     (BUILD.md v1 emits a single big glTF per import — Phase D may
///     split per top-level Collection later).</description></item>
///   <item><description>One <see cref="FlatMaterial"/> per
///     <see cref="StagedMaterial"/> referenced by any flat mesh.</description></item>
///   <item><description>A flat list of layer-path / source-object-id
///     pairs for the <c>scene_manifest.json</c> sidecar.</description></item>
/// </list>
///
/// The flattener is deliberately stateless and pure — it reads the
/// staged tree and returns immutable records. The glTF writer is the
/// only side-effecting consumer.
/// </summary>
public static class SceneFlattener
{
    public static FlatScene Flatten(StagedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var meshes = new List<FlatMesh>();
        var manifestEntries = new List<ManifestEntry>();
        var seenMaterials = new HashSet<string>(StringComparer.Ordinal);
        var materials = new List<FlatMaterial>();

        Walk(scene.Root, scene, meshes, manifestEntries, materials, seenMaterials);

        return new FlatScene(
            Version: scene.Version,
            Meshes: meshes,
            Materials: materials,
            Manifest: manifestEntries);
    }

    private static void Walk(
        StagedNode node,
        StagedScene scene,
        List<FlatMesh> meshes,
        List<ManifestEntry> manifest,
        List<FlatMaterial> materials,
        HashSet<string> seenMaterials)
    {
        switch (node)
        {
            case StagedCollection coll:
                manifest.Add(new ManifestEntry(
                    SourceObjectId: coll.SourceObjectId,
                    SpeckleType: coll.SpeckleType,
                    LayerPath: coll.LayerPath,
                    Kind: coll.Kind,
                    NodeIndex: -1));
                foreach (var child in coll.Children)
                {
                    Walk(child, scene, meshes, manifest, materials, seenMaterials);
                }
                break;

            case StagedMesh mesh:
                {
                    FlatMaterial? mat = null;
                    if (mesh.MaterialId is not null
                        && scene.Materials.TryGetValue(mesh.MaterialId, out var staged))
                    {
                        if (seenMaterials.Add(staged.SourceObjectId))
                        {
                            materials.Add(FlatMaterial.From(staged));
                        }
                        mat = materials.First(m => m.MaterialId == staged.SourceObjectId);
                    }
                    var nodeIndex = meshes.Count;
                    meshes.Add(new FlatMesh(
                        SourceObjectId: mesh.SourceObjectId,
                        LayerPath: mesh.LayerPath,
                        Mesh: mesh,
                        Material: mat));
                    manifest.Add(new ManifestEntry(
                        SourceObjectId: mesh.SourceObjectId,
                        SpeckleType: mesh.SpeckleType,
                        LayerPath: mesh.LayerPath,
                        Kind: mesh.Kind,
                        NodeIndex: nodeIndex));
                    break;
                }

            case StagedUnknown unknown:
                manifest.Add(new ManifestEntry(
                    SourceObjectId: unknown.SourceObjectId,
                    SpeckleType: unknown.SpeckleType,
                    LayerPath: unknown.LayerPath,
                    Kind: unknown.Kind,
                    NodeIndex: -1));
                break;

            case StagedMaterial:
                // Materials are indexed via StagedScene.Materials; they
                // never appear as tree leaves in our flat output.
                break;
        }
    }
}

public sealed record FlatScene(
    VersionDescriptor Version,
    IReadOnlyList<FlatMesh> Meshes,
    IReadOnlyList<FlatMaterial> Materials,
    IReadOnlyList<ManifestEntry> Manifest);

public sealed record FlatMesh(
    string SourceObjectId,
    string LayerPath,
    StagedMesh Mesh,
    FlatMaterial? Material);

public sealed record FlatMaterial(
    string MaterialId,
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
{
    public static FlatMaterial From(StagedMaterial m) => new(
        MaterialId: m.SourceObjectId,
        Name: m.Name,
        Diffuse: m.Diffuse,
        Emissive: m.Emissive,
        Opacity: m.Opacity,
        Roughness: m.Roughness,
        Metalness: m.Metalness,
        DiffuseTexturePath: m.DiffuseTexturePath,
        BaseColorTexturePath: m.BaseColorTexturePath,
        EmissiveTexturePath: m.EmissiveTexturePath,
        NormalTexturePath: m.NormalTexturePath);
}

public sealed record ManifestEntry(
    string SourceObjectId,
    string SpeckleType,
    string LayerPath,
    string Kind,
    int NodeIndex);
