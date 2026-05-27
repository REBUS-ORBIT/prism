using System.Numerics;
using System.Text.Json.Nodes;

using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Converters.FromOrbit;

/// <summary>
/// Converts <c>Objects.Geometry.Mesh</c> into a <see cref="StagedMesh"/>.
///
/// Speckle's mesh wire format is a pair of flat arrays plus an
/// inline-or-referenced material:
/// <list type="bullet">
///   <item><description><c>vertices</c>: <c>[x0,y0,z0, x1,y1,z1, ...]</c></description></item>
///   <item><description><c>faces</c>: variable-length encoding
///     <c>[n, i0..i(n-1), n, i0..., ...]</c> where <c>n</c> is the
///     face vertex count.</description></item>
///   <item><description><c>vertexNormals</c> (optional): same shape as vertices.</description></item>
///   <item><description><c>textureCoordinates</c> (optional): <c>[u0,v0, u1,v1, ...]</c>.</description></item>
///   <item><description><c>colors</c> (optional): packed ARGB ints, one per vertex.</description></item>
///   <item><description><c>renderMaterial</c> (optional): inline RenderMaterial body or
///     a <c>{referencedId, speckle_type:"reference"}</c> stub.</description></item>
/// </list>
///
/// Polygons with <c>n &gt; 3</c> triangulate fan-style around vertex
/// 0; <c>n == 3</c> is a triangle as-is. The Speckle Python SDK and
/// the REVIT connector use the same fan rule so the receive output
/// is bit-for-bit comparable to a Speckle-side bake.
/// </summary>
public sealed class MeshConverter : IFromOrbitConverter
{
    public const string OrbitTypeName = "Objects.Geometry.Mesh";

    public bool CanConvert(OrbitObject obj) =>
        obj.SpeckleType == OrbitTypeName;

    public StagedNode Convert(OrbitObject obj, ConversionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(ctx);

        var (vertices, normals, texCoords, colors) = ReadAttributes(obj.Raw);
        var indices = TriangulateFaces(obj.Raw);

        var materialId = ResolveMaterialReference(obj, ctx);

        return new StagedMesh(
            SourceObjectId: obj.Id,
            SpeckleType: obj.SpeckleType,
            LayerPath: ctx.LayerPath,
            Vertices: vertices,
            Indices: indices,
            Normals: normals,
            TexCoords: texCoords,
            Colors: colors,
            MaterialId: materialId);
    }

    /// <summary>
    /// Triangulate a Speckle face array into a flat <c>int[]</c> of
    /// vertex indices (groups of three forming triangles).
    /// </summary>
    public static IReadOnlyList<int> TriangulateFaces(JsonObject mesh)
    {
        var faces = mesh["faces"] as JsonArray;
        if (faces is null || faces.Count == 0) return Array.Empty<int>();

        var indices = new List<int>(capacity: faces.Count);
        var i = 0;
        while (i < faces.Count)
        {
            var n = faces[i]?.GetValue<int>()
                ?? throw new InvalidDataException(
                    $"Mesh face header at position {i} is null.");
            if (n < 3)
            {
                throw new InvalidDataException(
                    $"Mesh face at position {i} has degenerate vertex count {n} (must be >= 3).");
            }
            if (i + n >= faces.Count)
            {
                throw new InvalidDataException(
                    $"Mesh face at position {i} declares {n} vertices but the array runs out.");
            }

            var v0 = ReadIndex(faces, i + 1);
            for (int k = 1; k < n - 1; k++)
            {
                indices.Add(v0);
                indices.Add(ReadIndex(faces, i + 1 + k));
                indices.Add(ReadIndex(faces, i + 1 + k + 1));
            }
            i += n + 1;
        }
        return indices;
    }

    private static int ReadIndex(JsonArray faces, int pos) =>
        faces[pos]?.GetValue<int>()
            ?? throw new InvalidDataException(
                $"Mesh face index at position {pos} is null.");

    private static (
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<Vector3>? normals,
        IReadOnlyList<Vector2>? texCoords,
        IReadOnlyList<uint>? colors) ReadAttributes(JsonObject mesh)
    {
        var vertices = ReadVec3Array(mesh["vertices"] as JsonArray, required: true)
            ?? Array.Empty<Vector3>();
        var normals = ReadVec3Array(mesh["vertexNormals"] as JsonArray, required: false);
        var texCoords = ReadVec2Array(mesh["textureCoordinates"] as JsonArray);
        var colors = ReadColorArray(mesh["colors"] as JsonArray);
        return (vertices, normals, texCoords, colors);
    }

    private static IReadOnlyList<Vector3>? ReadVec3Array(JsonArray? arr, bool required)
    {
        if (arr is null)
        {
            if (required) throw new InvalidDataException("Mesh has no 'vertices' array.");
            return null;
        }
        if (arr.Count % 3 != 0)
        {
            throw new InvalidDataException(
                $"Mesh vec3 array length {arr.Count} is not divisible by 3.");
        }
        var result = new Vector3[arr.Count / 3];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Vector3(
                (float)(arr[i * 3]?.GetValue<double>() ?? 0d),
                (float)(arr[i * 3 + 1]?.GetValue<double>() ?? 0d),
                (float)(arr[i * 3 + 2]?.GetValue<double>() ?? 0d));
        }
        return result;
    }

    private static IReadOnlyList<Vector2>? ReadVec2Array(JsonArray? arr)
    {
        if (arr is null) return null;
        if (arr.Count % 2 != 0)
        {
            throw new InvalidDataException(
                $"Mesh vec2 array length {arr.Count} is not divisible by 2.");
        }
        var result = new Vector2[arr.Count / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Vector2(
                (float)(arr[i * 2]?.GetValue<double>() ?? 0d),
                (float)(arr[i * 2 + 1]?.GetValue<double>() ?? 0d));
        }
        return result;
    }

    private static IReadOnlyList<uint>? ReadColorArray(JsonArray? arr)
    {
        if (arr is null) return null;
        var result = new uint[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            // Speckle stores ARGB as a signed int in JSON. Cast through
            // long → uint to preserve the unsigned bit pattern.
            var raw = arr[i]?.GetValue<long>() ?? 0L;
            result[i] = unchecked((uint)raw);
        }
        return result;
    }

    /// <summary>
    /// Resolve the material reference attached to <paramref name="mesh"/>.
    /// Returns the source object id of a <see cref="StagedMaterial"/>
    /// the pipeline will later add to <see cref="StagedScene.Materials"/>,
    /// or <c>null</c> when the mesh has no material.
    /// </summary>
    private static string? ResolveMaterialReference(OrbitObject mesh, ConversionContext ctx)
    {
        var rmNode = mesh.Raw["renderMaterial"];
        if (rmNode is null) return null;

        // Reference stub: {referencedId, speckle_type:"reference"}
        if (rmNode is JsonObject rmObj
            && rmObj["referencedId"] is JsonValue refIdValue
            && refIdValue.TryGetValue<string>(out var refId)
            && !string.IsNullOrEmpty(refId))
        {
            // The pipeline will have added a StagedMaterial under refId
            // already (materials are converted before meshes that
            // reference them).
            return refId;
        }

        // Inline material: the mesh JSON carries the full RenderMaterial
        // body. The pipeline's pre-pass synthesises a deterministic
        // id ("{meshId}#renderMaterial") and adds the StagedMaterial
        // to the run-wide registry so meshes with inline materials
        // share a single glTF material when the body is identical.
        if (rmNode is JsonObject)
        {
            var inlineId = InlineMaterialId(mesh.Id);
            return inlineId;
        }
        return null;
    }

    /// <summary>Synthetic id used by the pipeline when registering inline materials.</summary>
    public static string InlineMaterialId(string meshId) => $"{meshId}#renderMaterial";
}
