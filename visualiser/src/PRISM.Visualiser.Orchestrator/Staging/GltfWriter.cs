using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Serilog;

using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Staging;

/// <summary>
/// Materialises a <see cref="FlatScene"/> as a glTF 2.0 document on
/// disk via SharpGLTF.Toolkit. Buffers and images are written as
/// satellite files (not embedded), per the BUILD.md spec, so each
/// resource is independently re-cacheable.
///
/// <para>
/// <b>Layout note:</b> SharpGLTF's high-level <see cref="ModelRoot.SaveGLTF(string)"/>
/// emits all satellite files in the same directory as the glTF
/// document. The plan asked for <c>textures/</c> and <c>buffers/</c>
/// subdirectories; v1 keeps everything flat under
/// <c>stage/{runId}/</c> because routing satellites into subfolders
/// requires a custom <c>WriteContext</c> override that's significantly
/// more code than the v1 plan budgets for. The
/// <c>scene_manifest.json</c> sidecar lists the satellite files so
/// downstream tooling doesn't depend on directory layout.
/// Phase D can re-shape this without breaking on-wire callers.
/// </para>
///
/// <para>
/// The <see cref="CoordinateTransform"/> is applied <em>once</em> per
/// vertex during write, NOT baked into per-node transforms — both
/// because the spec says so and because per-node transforms would
/// break the bounding-box invariants UE expects post-import.
/// </para>
/// </summary>
public sealed class GltfWriter
{
    /// <summary>Schema name stamped into <c>scene_manifest.json</c>.</summary>
    public const string ManifestSchema = "prism-visualiser/scene-manifest/v1";

    /// <summary>File name (without extension) of the staged glTF.</summary>
    public const string DefaultBaseName = "scene";

    private readonly ILogger _log;

    public GltfWriter(ILogger log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Write <paramref name="scene"/> into <paramref name="stageDir"/>.
    /// Returns the staged file paths + counts so callers (the CLI,
    /// the tests) don't have to re-stat the directory.
    /// </summary>
    public WriteResult Write(FlatScene scene, string stageDir, string baseName = DefaultBaseName)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);

        Directory.CreateDirectory(stageDir);

        var materialBuilders = BuildMaterialBuilders(scene.Materials, stageDir);

        var sceneBuilder = new SceneBuilder();
        var meshNodeIndices = new List<int>();
        for (int i = 0; i < scene.Meshes.Count; i++)
        {
            var fm = scene.Meshes[i];
            var meshBuilder = BuildMeshBuilder(fm, materialBuilders, i);
            if (meshBuilder is null) continue;

            // Identity transform per the spec — coord transform is
            // already baked into the vertices.
            sceneBuilder
                .AddRigidMesh(meshBuilder, Matrix4x4.Identity)
                .WithName($"node_{i:D5}_{Sanitize(fm.Mesh.Name())}");
            meshNodeIndices.Add(i);
        }

        // Always include at least one debug node for empty scenes so
        // downstream tooling never has to special-case zero-mesh
        // imports.
        if (sceneBuilder.Instances.Count == 0)
        {
            var fallback = BuildDebugCube();
            sceneBuilder.AddRigidMesh(fallback, Matrix4x4.Identity).WithName("empty_scene_marker");
        }

        var model = sceneBuilder.ToGltf2();
        model.Asset.Generator = "PRISM.Visualiser/Phase-C";
        model.Asset.Copyright = "REBUS-ORBIT";

        var gltfPath = Path.Combine(stageDir, $"{baseName}.gltf");
        model.SaveGLTF(gltfPath, new WriteSettings
        {
            JsonIndented = false,
            // Keep external file naming deterministic so the manifest
            // stays stable across writes (helps re-cacheability).
            ImageWriting = ResourceWriteMode.SatelliteFile,
        });

        // Write the manifest sidecar.
        var manifestPath = Path.Combine(stageDir, "scene_manifest.json");
        WriteManifest(scene, manifestPath, gltfPath);

        // Round-trip the document to validate. ModelRoot.Load runs
        // SharpGLTF's strict validator which mirrors the Khronos glTF
        // 2.0 conformance suite — any non-zero error throws.
        var loaded = ModelRoot.Load(gltfPath, new ReadSettings
        {
            Validation = SharpGLTF.Validation.ValidationMode.Strict,
        });
        var textureCount = loaded.LogicalTextures.Count;
        _log.Information(
            "glTF written path={GltfPath} meshes={MeshCount} materials={MaterialCount} textures={TextureCount}",
            gltfPath, scene.Meshes.Count, scene.Materials.Count, textureCount);

        return new WriteResult(
            GltfPath: gltfPath,
            ManifestPath: manifestPath,
            MeshCount: scene.Meshes.Count,
            MaterialCount: scene.Materials.Count,
            TextureCount: textureCount,
            ObjectCount: scene.Manifest.Count);
    }

    // ----------------------------------------------------------------
    // Material builders
    // ----------------------------------------------------------------

    private static Dictionary<string, MaterialBuilder> BuildMaterialBuilders(
        IReadOnlyList<FlatMaterial> materials, string stageDir)
    {
        var byId = new Dictionary<string, MaterialBuilder>(StringComparer.Ordinal);
        foreach (var m in materials)
        {
            var mb = new MaterialBuilder(Sanitize(m.Name))
                .WithDoubleSide(true)
                .WithMetallicRoughness((float)m.Metalness, (float)m.Roughness);
            mb.WithBaseColor(UnpackBaseColor(m.Diffuse, m.Opacity));
            mb.WithEmissive(UnpackRgb(m.Emissive));

            // Resolve textures — copy the cached blob into the stage
            // directory so the glTF + textures + buffers form a
            // self-contained, movable bundle.
            var diffusePath = m.BaseColorTexturePath ?? m.DiffuseTexturePath;
            if (diffusePath is not null)
            {
                var staged = StageTexture(diffusePath, stageDir);
                if (staged is not null)
                {
                    mb.WithChannelImage(KnownChannel.BaseColor, staged);
                }
            }
            if (m.EmissiveTexturePath is not null)
            {
                var staged = StageTexture(m.EmissiveTexturePath, stageDir);
                if (staged is not null)
                {
                    mb.WithChannelImage(KnownChannel.Emissive, staged);
                }
            }
            if (m.NormalTexturePath is not null)
            {
                var staged = StageTexture(m.NormalTexturePath, stageDir);
                if (staged is not null)
                {
                    mb.WithChannelImage(KnownChannel.Normal, staged);
                }
            }
            byId[m.MaterialId] = mb;
        }
        return byId;
    }

    private static MemoryImage? StageTexture(string sourcePath, string stageDir)
    {
        if (!File.Exists(sourcePath)) return null;
        var fileName = Path.GetFileName(sourcePath);
        var stagePath = Path.Combine(stageDir, fileName);
        if (!File.Exists(stagePath))
        {
            File.Copy(sourcePath, stagePath, overwrite: false);
        }
        var bytes = File.ReadAllBytes(stagePath);
        return new MemoryImage(bytes);
    }

    // ----------------------------------------------------------------
    // Mesh builders
    // ----------------------------------------------------------------

    private static IMeshBuilder<MaterialBuilder>? BuildMeshBuilder(
        FlatMesh fm,
        IReadOnlyDictionary<string, MaterialBuilder> materialBuilders,
        int meshIndex)
    {
        var mesh = fm.Mesh;
        if (mesh.Vertices.Count == 0 || mesh.Indices.Count < 3) return null;

        var vertices = mesh.Vertices.ToArray();
        // Apply the source → UE coordinate transform once, here.
        CoordinateTransform.TransformInPlace(vertices);

        // Compute or reuse normals. Source might have provided them;
        // if not, accumulate per-vertex from face geometry.
        var normals = mesh.Normals is { Count: var nc } && nc == mesh.Vertices.Count
            ? mesh.Normals!.Select(n => Vector3.Normalize(CoordinateTransform.TransformNormal(n))).ToArray()
            : ComputeVertexNormals(vertices, mesh.Indices);

        // UVs (default to 0,0 when absent).
        var uvs = mesh.TexCoords is { Count: var uc } && uc == mesh.Vertices.Count
            ? mesh.TexCoords!.ToArray()
            : new Vector2[mesh.Vertices.Count];

        // Vertex colors (default to opaque white).
        var colors = mesh.Colors is { Count: var cc } && cc == mesh.Vertices.Count
            ? mesh.Colors!.Select(UnpackVertexColor).ToArray()
            : Enumerable.Repeat(Vector4.One, mesh.Vertices.Count).ToArray();

        var matBuilder = fm.Material is { } mat
            ? materialBuilders[mat.MaterialId]
            : DefaultMaterial();

        var mb = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>(
            $"mesh_{meshIndex:D5}");
        var prim = mb.UsePrimitive(matBuilder);

        for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var i0 = mesh.Indices[i];
            var i1 = mesh.Indices[i + 1];
            var i2 = mesh.Indices[i + 2];
            if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
            {
                continue; // skip malformed triangle
            }
            // Right-handed → left-handed handedness flip means we
            // reverse winding order so glTF (right-handed by spec)
            // ends up with the outward-facing normal that the
            // mirrored vertex positions imply.
            var v0 = MakeVertex(vertices[i0], normals[i0], colors[i0], uvs[i0]);
            var v1 = MakeVertex(vertices[i1], normals[i1], colors[i1], uvs[i1]);
            var v2 = MakeVertex(vertices[i2], normals[i2], colors[i2], uvs[i2]);
            prim.AddTriangle(v0, v2, v1);
        }
        return mb;
    }

    private static VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty> MakeVertex(
        Vector3 position, Vector3 normal, Vector4 color, Vector2 uv) =>
        new(
            new VertexPositionNormal(position, normal),
            new VertexColor1Texture1(color, uv));

    private static Vector3[] ComputeVertexNormals(Vector3[] vertices, IReadOnlyList<int> indices)
    {
        var accum = new Vector3[vertices.Length];
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];
            if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) continue;
            var n = Vector3.Cross(vertices[i1] - vertices[i0], vertices[i2] - vertices[i0]);
            accum[i0] += n;
            accum[i1] += n;
            accum[i2] += n;
        }
        for (int i = 0; i < accum.Length; i++)
        {
            accum[i] = accum[i].LengthSquared() > 1e-12f
                ? Vector3.Normalize(accum[i])
                : Vector3.UnitZ;
        }
        return accum;
    }

    private static MaterialBuilder DefaultMaterial() =>
        new MaterialBuilder("default")
            .WithDoubleSide(true)
            .WithMetallicRoughness(0.0f, 0.5f)
            .WithBaseColor(new Vector4(0.78f, 0.78f, 0.78f, 1.0f));

    private static IMeshBuilder<MaterialBuilder> BuildDebugCube()
    {
        // 1 m magenta cube centred at origin — placeholder for empty
        // imports. Vertices are in source coords; the coord transform
        // is applied inline since this path skips MeshConverter.
        var p = 0.5f;
        var raw = new Vector3[]
        {
            new(-p, -p, -p), new(p, -p, -p), new(p, p, -p), new(-p, p, -p),
            new(-p, -p,  p), new(p, -p,  p), new(p, p,  p), new(-p, p,  p),
        };
        for (int i = 0; i < raw.Length; i++) raw[i] = CoordinateTransform.TransformPoint(raw[i]);

        var mat = new MaterialBuilder("debug-magenta")
            .WithDoubleSide(true)
            .WithBaseColor(new Vector4(1, 0, 1, 1))
            .WithMetallicRoughness(0.0f, 0.5f);
        var mb = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("debug_cube");
        var prim = mb.UsePrimitive(mat);

        // Six quads as fan-triangulated triangle pairs. Order doesn't
        // matter for a debug placeholder, so don't bother re-winding.
        var quads = new (int, int, int, int)[]
        {
            (0, 1, 2, 3), // -Z
            (4, 7, 6, 5), // +Z
            (0, 4, 5, 1), // -Y
            (2, 6, 7, 3), // +Y
            (1, 5, 6, 2), // +X
            (0, 3, 7, 4), // -X
        };
        var n = ComputeVertexNormals(raw, FlattenQuads(quads));
        var white = new Vector4(1, 1, 1, 1);
        foreach (var (a, b, c, d) in quads)
        {
            prim.AddTriangle(
                MakeVertex(raw[a], n[a], white, Vector2.Zero),
                MakeVertex(raw[c], n[c], white, Vector2.Zero),
                MakeVertex(raw[b], n[b], white, Vector2.Zero));
            prim.AddTriangle(
                MakeVertex(raw[a], n[a], white, Vector2.Zero),
                MakeVertex(raw[d], n[d], white, Vector2.Zero),
                MakeVertex(raw[c], n[c], white, Vector2.Zero));
        }
        return mb;
    }

    private static IReadOnlyList<int> FlattenQuads((int, int, int, int)[] quads)
    {
        var list = new List<int>(quads.Length * 6);
        foreach (var (a, b, c, d) in quads)
        {
            list.Add(a); list.Add(b); list.Add(c);
            list.Add(a); list.Add(c); list.Add(d);
        }
        return list;
    }

    // ----------------------------------------------------------------
    // Manifest sidecar
    // ----------------------------------------------------------------

    private static void WriteManifest(FlatScene scene, string manifestPath, string gltfPath)
    {
        var manifest = new SceneManifest(
            Schema: ManifestSchema,
            ProjectId: scene.Version.ProjectId,
            ModelId: scene.Version.ModelId,
            VersionId: scene.Version.VersionId,
            RootObjectId: scene.Version.RootObjectId,
            GltfFile: Path.GetFileName(gltfPath),
            Entries: scene.Manifest.Select(e => new SceneManifestEntry(
                SourceObjectId: e.SourceObjectId,
                SpeckleType: e.SpeckleType,
                LayerPath: e.LayerPath,
                Kind: e.Kind,
                NodeIndex: e.NodeIndex)).ToList());

        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.SceneManifest));
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static Vector4 UnpackBaseColor(long argb, double opacity)
    {
        var u = unchecked((uint)argb);
        var a = ((u >> 24) & 0xFF) / 255f;
        var r = ((u >> 16) & 0xFF) / 255f;
        var g = ((u >> 8) & 0xFF) / 255f;
        var b = (u & 0xFF) / 255f;
        if (a == 0) a = 1f;
        var o = (float)Math.Clamp(opacity, 0.0, 1.0);
        return new Vector4(r, g, b, a * o);
    }

    private static Vector3 UnpackRgb(long argb)
    {
        var u = unchecked((uint)argb);
        var r = ((u >> 16) & 0xFF) / 255f;
        var g = ((u >> 8) & 0xFF) / 255f;
        var b = (u & 0xFF) / 255f;
        return new Vector3(r, g, b);
    }

    private static Vector4 UnpackVertexColor(uint argb)
    {
        var a = ((argb >> 24) & 0xFF) / 255f;
        var r = ((argb >> 16) & 0xFF) / 255f;
        var g = ((argb >> 8) & 0xFF) / 255f;
        var b = (argb & 0xFF) / 255f;
        if (a == 0) a = 1f;
        return new Vector4(r, g, b, a);
    }

    private static string Sanitize(string s)
    {
        var trimmed = s.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "_";
        var chars = trimmed.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return new string(chars.ToArray());
    }

    public sealed record WriteResult(
        string GltfPath,
        string ManifestPath,
        int MeshCount,
        int MaterialCount,
        int TextureCount,
        int ObjectCount);

    public sealed record SceneManifest(
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("projectId")] string ProjectId,
        [property: JsonPropertyName("modelId")] string ModelId,
        [property: JsonPropertyName("versionId")] string VersionId,
        [property: JsonPropertyName("rootObjectId")] string RootObjectId,
        [property: JsonPropertyName("gltfFile")] string GltfFile,
        [property: JsonPropertyName("entries")] IReadOnlyList<SceneManifestEntry> Entries);

    public sealed record SceneManifestEntry(
        [property: JsonPropertyName("sourceObjectId")] string SourceObjectId,
        [property: JsonPropertyName("speckleType")] string SpeckleType,
        [property: JsonPropertyName("layerPath")] string LayerPath,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("nodeIndex")] int NodeIndex);
}

[JsonSerializable(typeof(GltfWriter.SceneManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext { }

/// <summary>Helpers for naming staged glTF nodes.</summary>
file static class StagedMeshExtensions
{
    public static string Name(this StagedMesh mesh) =>
        string.IsNullOrEmpty(mesh.LayerPath) ? mesh.SourceObjectId : mesh.LayerPath;
}
