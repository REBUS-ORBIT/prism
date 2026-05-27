using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Converters.FromOrbit;

/// <summary>
/// Converts a single ORBIT object into a <see cref="StagedNode"/> the
/// glTF writer understands.
///
/// Collections are deliberately NOT handled by an <see cref="IFromOrbitConverter"/>
/// because collection conversion is mutually recursive with the
/// pipeline's tree walk (you can't dispatch the children without
/// running the dispatch table again). The pipeline owns that logic;
/// converters only see leaves: <see cref="StagedMesh"/>,
/// <see cref="StagedMaterial"/>, or <see cref="StagedUnknown"/>.
/// </summary>
public interface IFromOrbitConverter
{
    /// <summary>
    /// Returns true if this converter can produce a <see cref="StagedNode"/>
    /// for <paramref name="obj"/>. Implementations should match on
    /// <see cref="OrbitObject.SpeckleType"/> exactly (no prefix
    /// matching) — see <see cref="OrbitObject.SpeckleType"/> remarks.
    /// </summary>
    bool CanConvert(OrbitObject obj);

    /// <summary>
    /// Convert <paramref name="obj"/> to a <see cref="StagedNode"/>.
    /// Caller guarantees <see cref="CanConvert"/> returned true for
    /// the same object.
    /// </summary>
    StagedNode Convert(OrbitObject obj, ConversionContext ctx);
}
