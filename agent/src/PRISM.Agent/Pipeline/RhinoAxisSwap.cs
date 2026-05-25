using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace PRISM.Agent.Pipeline;

/// <summary>
/// In-place coordinate-frame transforms applied to a freshly-opened
/// <see cref="RhinoDoc"/> before the connector send pipeline runs.
///
/// The single transform currently supported is the +90° rotation about X
/// exposed by the convert UI as "Swap Y/Z axes". Despite the user-facing
/// label and the historical method name <see cref="ApplyYZSwap"/>, this
/// is a proper rotation (det = +1), not a reflection: the legacy
/// 3DConvert pipeline shipped a reflection (<c>(x,y,z) → (x,z,y)</c>)
/// which inverted handedness and caused OBJ uploads to land in ORBIT
/// visually mirrored about the vertical axis (front-facing geometry
/// rendered as if seen from behind, with texture UVs flipped on the
/// same axis). v0.1.25 replaced the reflection with an Rx(-90°)
/// rotation, but with our standard test bundle (a Y-up OBJ that
/// Rhino's <c>FileObj.Read</c> does not re-orient on import) that
/// landed upside-down; v0.1.26 flips the sign to Rx(+90°), which is
/// empirically what the bundle needs.
///
/// The transform is applied via <c>doc.Objects.Transform(id, swap, true)</c>.
/// Choosing the doc-table transform (rather than mutating geometry directly)
/// means block instance placements ride along with the rotation — the
/// connector's <c>RhinoInstanceConverter</c> reads the up-to-date
/// <c>InstanceXform</c> off the InstanceObject, so block contents come
/// out rotated too.
///
/// Replacing the object with <c>deleteOriginal: true</c> invalidates any
/// cached render meshes on the original GUID. The connector subsequently
/// calls <see cref="RhinoObject.CreateMeshes(MeshType, MeshingParameters, bool)"/>
/// against the new GUID and tessellates the already-rotated geometry, so
/// the wire mesh data lands in ORBIT with the new axes baked in.
/// </summary>
public static class RhinoAxisSwap
{
    /// <summary>
    /// Rotate +90° about X (swap Y↔Z with sign flip; converts Y-up source
    /// to ORBIT viewer's Y-up convention while preserving front-facing
    /// geometry). The public method name is kept as <c>ApplyYZSwap</c>
    /// for backwards compatibility with the WS contract and convert-UI
    /// label, but the matrix is a rotation, not a reflection.
    /// Matrix (RhinoCommon stores row-major, applies <c>T · point</c>):
    /// <code>
    /// | 1  0  0 0 |   | x |   |  x |
    /// | 0  0 -1 0 | · | y | = | -z |
    /// | 0  1  0 0 |   | z |   |  y |
    /// | 0  0  0 1 |   | 1 |   |  1 |
    /// </code>
    /// Determinant is +1 — handedness is preserved, so triangle winding,
    /// surface normals, and texture UVs stay self-consistent. Right-hand
    /// rule for <c>Rx(+90°)</c>: <c>+Y → +Z</c>, <c>+Z → -Y</c>. With our
    /// standard test bundle (Y-up OBJ, imported without re-orientation by
    /// Rhino) this lands the model right-side-up in the ORBIT viewer.
    /// </summary>
    public static void ApplyYZSwap(RhinoDoc doc, Action<string>? log = null)
    {
        var swap = Transform.Identity;
        swap.M11 = 0; swap.M12 = -1; // new Y row → reads from -(old Z)
        swap.M21 = 1; swap.M22 = 0;  // new Z row → reads from old Y

        // Snapshot ids first — transforming with deleteOriginal=true
        // mutates the object table and would invalidate any in-flight
        // enumerator.
        Guid[] ids;
        try
        {
            ids = doc.Objects
                .Where(o => o is not null && !o.IsDeleted)
                .Select(o => o.Id)
                .ToArray();
        }
        catch (Exception ex)
        {
            log?.Invoke($"[SWAP-YZ] failed to enumerate doc objects: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        int ok = 0, failed = 0;
        foreach (var id in ids)
        {
            try
            {
                var newId = doc.Objects.Transform(id, swap, deleteOriginal: true);
                if (newId != Guid.Empty) ok++;
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                log?.Invoke($"[SWAP-YZ] transform threw for object {id}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        log?.Invoke($"[SWAP-YZ] Rotate +90° about X (swap Y↔Z with sign flip; converts Y-up source to ORBIT viewer's Y-up convention while preserving front-facing geometry) applied to {ok}/{ids.Length} objects (failed={failed})");
    }
}
