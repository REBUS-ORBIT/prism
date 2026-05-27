using System.Numerics;

namespace PRISM.Visualiser.Orchestrator.Staging;

/// <summary>
/// Converts between ORBIT/Speckle world coordinates and the UE world
/// coordinates the staged glTF must match.
///
/// <list type="bullet">
///   <item><description>
///     ORBIT/Speckle: right-handed, Z-up, model units (typically m or
///     mm — Phase C assumes the source object's reported units).
///   </description></item>
///   <item><description>
///     Unreal Engine: left-handed, Z-up, 1 UE unit = 1 cm.
///   </description></item>
/// </list>
///
/// Net transform (per the BUILD.md spec):
/// <code>
///   X_ue =  X_speckle * 100
///   Y_ue = -Y_speckle * 100   ← mirror Y to flip handedness
///   Z_ue =  Z_speckle * 100
/// </code>
///
/// <para>
/// Cross-checked against <c>ConversionContext.cs</c> in the Rhino
/// connector (vendor/orbit-monorepo path): Rhino sends/receives in
/// the document's native units without forcing a scale on the wire,
/// so the connector's "canonical" numbers don't override Phase C.
/// The visualiser is the first writer to UE coords, so the BUILD.md
/// spec values stand.
/// </para>
///
/// Per the spec, the transform is applied <em>once</em> to vertex
/// positions during glTF write, NOT baked into per-node transforms.
/// </summary>
public static class CoordinateTransform
{
    /// <summary>UE units per ORBIT metre.</summary>
    public const float UeUnitsPerMetre = 100.0f;

    /// <summary>
    /// Apply the source-to-UE transform to a single point. Caller is
    /// responsible for applying the source-units → metres scale
    /// before this call (the source mesh's vertices are already in
    /// metres for v1; Phase D will thread units through here).
    /// </summary>
    public static Vector3 TransformPoint(Vector3 p) =>
        new(p.X * UeUnitsPerMetre, -p.Y * UeUnitsPerMetre, p.Z * UeUnitsPerMetre);

    /// <summary>
    /// Apply the same transform to a <see cref="Vector3"/> array, in
    /// place. Used by the glTF writer's hot path.
    /// </summary>
    public static void TransformInPlace(Vector3[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = TransformPoint(points[i]);
        }
    }

    /// <summary>
    /// Transform a normal. Same handedness flip as the position but no
    /// scale (lengths are preserved by glTF spec; the writer
    /// re-normalises after if needed).
    /// </summary>
    public static Vector3 TransformNormal(Vector3 n) => new(n.X, -n.Y, n.Z);

    /// <summary>
    /// Materialise the transform as a <see cref="Matrix4x4"/> for use
    /// by the SharpGLTF builders (e.g. as a node transform when bulk
    /// transforming a sub-tree). Phase C's writer applies the
    /// per-vertex form instead — see class remarks — but the matrix
    /// is exposed for tests and for Phase D's instance-baking path.
    /// </summary>
    public static Matrix4x4 ToMatrix() =>
        new(
            UeUnitsPerMetre, 0, 0, 0,
            0, -UeUnitsPerMetre, 0, 0,
            0, 0, UeUnitsPerMetre, 0,
            0, 0, 0, 1);
}
