using Xunit;

using PRISM.Visualiser.Orchestrator.Converters.FromOrbit;
using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.Pipeline;
using PRISM.Visualiser.Orchestrator.Tests.TestHelpers;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 6 — fallback converter + sidecar log.
///
/// An object of type <c>Orbit.Objects.Lighting.MvrScene</c>
/// (deliberately unknown to Phase C — MVR support lands in Phase J)
/// goes through the <see cref="FallbackConverter"/>, gets recorded in
/// <c>unknown_objects.jsonl</c>, and the pipeline finishes without
/// crashing.
/// </summary>
public class FallbackConverterTests
{
    [Fact]
    public async Task UnknownType_FallsBackAndLogsToSidecar()
    {
        using var env = new TestEnv();

        var api = new FakeOrbitApi();
        api.RegisterVersion(new VersionDescriptor("p1", "m1", "v1", RootObjectId: "root"));

        // The unknown object is an MVR scene placeholder.
        api.RegisterObject("mvr-1", TestEnv.MakeUnknown("mvr-1", "Orbit.Objects.Lighting.MvrScene"));

        // Plus a normal mesh next to it so the pipeline has at least
        // one happy-path conversion to round-trip.
        api.RegisterObject("mesh-1", TestEnv.MakeMesh(
            id: "mesh-1",
            vertices: new double[] { 0, 0, 0,  1, 0, 0,  0, 1, 0 },
            faces: new[] { 3, 0, 1, 2 }));
        api.RegisterObject("root", TestEnv.MakeCollection(
            "root", "Root", new[] { "mvr-1", "mesh-1" }));

        var sink = env.CreateInMemoryUnknownSink();
        var pipeline = new OrbitReceivePipeline(
            api, env.ContentCache, env.CreateBlobDownloader(api),
            sink, env.Logger);

        // Pipeline does NOT throw on unknown types.
        var scene = await pipeline.ReceiveAsync("p1", "v1", default);

        // The mvr-1 object surfaces as a StagedUnknown.
        Assert.Single(scene.Unknowns);
        var unknown = scene.Unknowns[0];
        Assert.Equal("mvr-1", unknown.SourceObjectId);
        Assert.Equal("Orbit.Objects.Lighting.MvrScene", unknown.SpeckleType);

        // The sidecar sink recorded an entry with type + objectId.
        var entries = sink.Entries;
        Assert.Single(entries);
        Assert.Equal("mvr-1", entries[0].ObjectId);
        Assert.Equal("Orbit.Objects.Lighting.MvrScene", entries[0].SpeckleType);
        Assert.Equal(UnknownObjectSink.SchemaName, entries[0].Schema);
    }

    [Fact]
    public void DirectFallback_RecordsAndProducesStagedUnknown()
    {
        var orbit = OrbitObject.From(TestEnv.MakeUnknown("x", "Foo.Bar.Baz"));
        var sink = new UnknownObjectSink();
        var ctx = new ConversionContext
        {
            ProjectId = "p",
            LayerPath = "Root::Group",
            ObjectsById = new Dictionary<string, OrbitObject>(),
            BlobPaths = new Dictionary<string, string>(),
            Unknowns = sink,
            Logger = Serilog.Core.Logger.None,
        };

        var converter = new FallbackConverter();
        Assert.True(converter.CanConvert(orbit));
        var node = converter.Convert(orbit, ctx);

        var unknown = Assert.IsType<StagedUnknown>(node);
        Assert.Equal("x", unknown.SourceObjectId);
        Assert.Equal("Foo.Bar.Baz", unknown.SpeckleType);

        Assert.Single(sink.Entries);
        Assert.Equal("Root::Group", sink.Entries[0].LayerPath);
    }
}
