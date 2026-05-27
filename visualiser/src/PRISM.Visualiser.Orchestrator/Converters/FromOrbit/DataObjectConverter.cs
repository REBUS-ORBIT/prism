using System.Text.Json.Nodes;

using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Converters.FromOrbit;

/// <summary>
/// Catch-all converter for ORBIT objects that are NOT meshes themselves
/// but carry a <c>displayValue</c> array of meshes (or mesh refs). This
/// covers the Speckle <c>Objects.Geometry.Brep</c>,
/// <c>Objects.Geometry.NurbsCurve</c>, and the family of higher-level
/// data classes (e.g. <c>Speckle.DataObject</c>, BIM elements) that
/// stream a baked mesh as their viewer-friendly preview.
///
/// Behaviour: the object collapses to a <see cref="StagedCollection"/>
/// containing one <see cref="StagedMesh"/> per displayValue entry. The
/// non-mesh native geometry (Brep faces, NURBS curves) is dropped — UE
/// can't render those directly anyway.
/// </summary>
public sealed class DataObjectConverter : IFromOrbitConverter
{
    private readonly MeshConverter _meshConverter;

    public DataObjectConverter(MeshConverter meshConverter)
    {
        _meshConverter = meshConverter ?? throw new ArgumentNullException(nameof(meshConverter));
    }

    public bool CanConvert(OrbitObject obj)
    {
        if (obj.SpeckleType == MeshConverter.OrbitTypeName) return false;
        if (obj.SpeckleType == MaterialConverter.OrbitTypeName) return false;
        return obj.Raw["displayValue"] is JsonArray { Count: > 0 };
    }

    public StagedNode Convert(OrbitObject obj, ConversionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(ctx);

        var displayValue = obj.Raw["displayValue"] as JsonArray;
        var children = new List<StagedNode>();
        if (displayValue is not null)
        {
            foreach (var entry in displayValue)
            {
                var mesh = ResolveDisplayValueEntry(entry, obj.Id, ctx);
                if (mesh is null) continue;

                var node = _meshConverter.Convert(mesh, ctx);
                children.Add(node);
            }
        }

        if (children.Count == 0)
        {
            // No usable geometry — log to the unknown sidecar so the
            // object is visible in offline triage but DON'T fail the
            // run. A Brep with an empty displayValue is rare but real.
            ctx.Unknowns.Record(obj.Id, obj.SpeckleType, ctx.LayerPath);
            return new StagedUnknown(
                SourceObjectId: obj.Id,
                SpeckleType: obj.SpeckleType,
                LayerPath: ctx.LayerPath,
                RawJson: obj.Raw.ToJsonString());
        }

        return new StagedCollection(
            SourceObjectId: obj.Id,
            SpeckleType: obj.SpeckleType,
            Name: obj.Name ?? obj.SpeckleType,
            LayerPath: ctx.LayerPath,
            Children: children);
    }

    /// <summary>
    /// A displayValue entry is either a full inline mesh body or a
    /// reference stub. Resolve to a concrete <see cref="OrbitObject"/>
    /// the mesh converter can chew on; returns null when the entry
    /// is something else (e.g. a Curve with no mesh fallback).
    /// </summary>
    private static OrbitObject? ResolveDisplayValueEntry(
        JsonNode? entry, string parentId, ConversionContext ctx)
    {
        if (entry is not JsonObject obj) return null;

        // Reference stub.
        if (obj["referencedId"] is JsonValue v
            && v.TryGetValue<string>(out var refId)
            && !string.IsNullOrEmpty(refId)
            && ctx.ObjectsById.TryGetValue(refId, out var resolved))
        {
            return resolved.SpeckleType == MeshConverter.OrbitTypeName ? resolved : null;
        }

        // Inline body.
        var speckleType = obj["speckle_type"]?.GetValue<string>()
                       ?? obj["type"]?.GetValue<string>()
                       ?? string.Empty;
        if (speckleType != MeshConverter.OrbitTypeName) return null;

        var inlineId = obj["id"]?.GetValue<string>() ?? $"{parentId}#displayValue";
        return new OrbitObject(inlineId, speckleType, name: null, obj);
    }

    /// <summary>
    /// Speckle units string → metres scale factor. Phase C threads
    /// this through <see cref="Staging.CoordinateTransform"/> rather
    /// than baking it into the converter so the staged mesh keeps the
    /// source coordinates verbatim.
    /// </summary>
    public static float UnitsToMetres(string? units) => units switch
    {
        "mm" => 0.001f,
        "cm" => 0.01f,
        "m" => 1.0f,
        "in" => 0.0254f,
        "ft" => 0.3048f,
        _ => 1.0f, // unknown: trust the source
    };
}
