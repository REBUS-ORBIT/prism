using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Converters.FromOrbit;

/// <summary>
/// Last-resort converter for ORBIT object types no other converter
/// recognises. Emits a <see cref="StagedUnknown"/> AND records the
/// object to <see cref="UnknownObjectSink"/> so an offline triage
/// pass (or a Phase D regression) can surface the type without
/// rerunning the receive pipeline.
///
/// In glTF land the fallback materialises as a debug-coloured cube
/// mesh — see <see cref="Staging.GltfWriter"/>'s fallback geometry
/// path. The cube is intentionally bright magenta + 100 cm wide so
/// it's loud in the viewer, signalling "you're missing a converter
/// for this type" without crashing the import.
/// </summary>
public sealed class FallbackConverter : IFromOrbitConverter
{
    /// <summary>Returns true for every object — used last in the dispatch chain.</summary>
    public bool CanConvert(OrbitObject obj) => true;

    public StagedNode Convert(OrbitObject obj, ConversionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.Unknowns.Record(obj.Id, obj.SpeckleType, ctx.LayerPath);
        ctx.Logger.Warning(
            "FromOrbit: no converter for type={SpeckleType} id={ObjectId}; falling back to debug-cube",
            obj.SpeckleType, obj.Id);

        return new StagedUnknown(
            SourceObjectId: obj.Id,
            SpeckleType: obj.SpeckleType,
            LayerPath: ctx.LayerPath,
            RawJson: obj.Raw.ToJsonString());
    }
}
