using Serilog.Core;

using Xunit;

using PRISM.Visualiser.Orchestrator.Converters.FromOrbit;
using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.Tests.TestHelpers;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 3 — Speckle mesh face triangulation.
///
/// A mesh with three quads round-trips through <see cref="MeshConverter"/>
/// to six glTF triangles (two per quad). Vertex count is preserved
/// (the triangulation only adds index entries).
/// </summary>
public class MeshConverterTests
{
    [Fact]
    public void ThreeQuads_TriangulateToSixTriangles()
    {
        // 12 vertices (3 quads × 4 vertices), positions don't matter
        // for the triangulation assertion — just need 36 doubles.
        var vertices = Enumerable.Range(0, 12)
            .SelectMany(i => new double[] { i, i, i })
            .ToArray();

        // Speckle face encoding: each face starts with vertex count.
        // [4, 0, 1, 2, 3,     ← quad 0
        //  4, 4, 5, 6, 7,     ← quad 1
        //  4, 8, 9, 10, 11]   ← quad 2
        var faces = new[]
        {
            4, 0, 1, 2, 3,
            4, 4, 5, 6, 7,
            4, 8, 9, 10, 11,
        };

        var meshJson = TestEnv.MakeMesh("mesh-3quads", vertices, faces);
        var orbit = OrbitObject.From(meshJson);

        var converter = new MeshConverter();
        Assert.True(converter.CanConvert(orbit));

        var ctx = new ConversionContext
        {
            ProjectId = "p",
            LayerPath = "root",
            ObjectsById = new Dictionary<string, OrbitObject>(),
            BlobPaths = new Dictionary<string, string>(),
            Unknowns = new UnknownObjectSink(),
            Logger = Logger.None,
        };

        var node = converter.Convert(orbit, ctx);
        var staged = Assert.IsType<StagedMesh>(node);

        Assert.Equal(12, staged.Vertices.Count);
        // 3 quads × 2 triangles per quad × 3 indices per triangle = 18.
        Assert.Equal(18, staged.Indices.Count);
        Assert.Equal(0, staged.Indices.Count % 3);
    }

    [Fact]
    public void Triangle_TriangulationIsIdentity()
    {
        var vertices = new double[] { 0, 0, 0,  1, 0, 0,  0, 1, 0 };
        var faces = new[] { 3, 0, 1, 2 };
        var meshJson = TestEnv.MakeMesh("mesh-tri", vertices, faces);
        var orbit = OrbitObject.From(meshJson);

        var converter = new MeshConverter();
        var ctx = new ConversionContext
        {
            ProjectId = "p",
            LayerPath = string.Empty,
            ObjectsById = new Dictionary<string, OrbitObject>(),
            BlobPaths = new Dictionary<string, string>(),
            Unknowns = new UnknownObjectSink(),
            Logger = Logger.None,
        };
        var staged = (StagedMesh)converter.Convert(orbit, ctx);
        Assert.Equal(new[] { 0, 1, 2 }, staged.Indices.ToArray());
    }
}
