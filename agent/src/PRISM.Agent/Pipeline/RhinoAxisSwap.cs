using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace PRISM.Agent.Pipeline;

/// <summary>
/// In-place coordinate-frame transforms applied to a freshly-opened
/// <see cref="RhinoDoc"/> before the connector send pipeline runs.
///
/// The single transform currently supported is the Y↔Z swap exposed by
/// the convert UI as "Swap Y/Z axes". It mirrors the matching feature in
/// the legacy 3DConvert pipeline (per-vertex <c>(x,y,z) → (x,z,y)</c> in
/// the IronPython exporter): used when a source file authored in a Y-up
/// content tool (Blender / Unity / a Y-up OBJ export) needs its axes
/// rotated into Rhino's Z-up convention before it lands in ORBIT.
///
/// The swap is implemented as a single <see cref="Transform"/> matrix
/// applied via <c>doc.Objects.Transform(id, swap, true)</c>. Choosing the
/// doc-table transform (rather than mutating geometry directly) means
/// block instance placements ride along with the swap — the connector's
/// <c>RhinoInstanceConverter</c> reads the up-to-date <c>InstanceXform</c>
/// off the InstanceObject, so block contents come out swapped too.
///
/// Replacing the object with <c>deleteOriginal: true</c> invalidates any
/// cached render meshes on the original GUID. The connector subsequently
/// calls <see cref="RhinoObject.CreateMeshes(MeshType, MeshingParameters, bool)"/>
/// against the new GUID and tessellates the already-swapped geometry, so
/// the wire mesh data lands in ORBIT with the new axes baked in.
/// </summary>
public static class RhinoAxisSwap
{
    /// <summary>
    /// Apply a Y↔Z swap to every object in <paramref name="doc"/>.
    /// Matrix (RhinoCommon stores row-major, applies <c>T · point</c>):
    /// <code>
    /// | 1 0 0 0 |   | x |   | x |
    /// | 0 0 1 0 | · | y | = | z |
    /// | 0 1 0 0 |   | z |   | y |
    /// | 0 0 0 1 |   | 1 |   | 1 |
    /// </code>
    /// Determinant is -1 (a reflection), matching the legacy 3DConvert
    /// vertex remap which was also a reflection — not a 90° rotation.
    /// Net effect: vertices, normals, and block placements that were
    /// authored Y-up become Z-up in the doc, ready for the connector.
    /// </summary>
    public static void ApplyYZSwap(RhinoDoc doc, Action<string>? log = null)
    {
        var swap = Transform.Identity;
        swap.M11 = 0; swap.M12 = 1; // new Y row → reads from old Z
        swap.M21 = 1; swap.M22 = 0; // new Z row → reads from old Y

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

        log?.Invoke($"[SWAP-YZ] applied Y↔Z swap to {ok}/{ids.Length} objects (failed={failed})");
    }
}
