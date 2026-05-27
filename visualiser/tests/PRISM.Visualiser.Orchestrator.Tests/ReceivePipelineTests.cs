using System.Text.Json.Nodes;

using Xunit;

using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.Pipeline;
using PRISM.Visualiser.Orchestrator.Tests.TestHelpers;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Tests 1 & 2 — receive-pipeline integration over the full
/// (mocked) wire path:
///   * Test 1: cache-hit / cache-miss flow — second receive does
///             not re-fetch any object from the API.
///   * Test 2: 5-deep nested Collection with 50 leaf meshes round-
///             trips through <see cref="OrbitReceivePipeline.ReceiveAsync"/>
///             with the input topology preserved.
/// </summary>
public class ReceivePipelineTests
{
    [Fact]
    public async Task Test1_FirstReceiveHitsApi_SecondHitsCache()
    {
        using var env = new TestEnv();
        var (api, projectId, versionId, expectedObjectCount) = BuildSmallScene();

        var pipeline = NewPipeline(env, api);

        // First call: every object goes through HTTP.
        var first = await pipeline.ReceiveAsync(projectId, versionId, default);
        Assert.NotNull(first);
        Assert.Equal(expectedObjectCount, api.ObjectCalls);
        Assert.Equal(1, api.VersionCalls);

        // Second call (fresh pipeline, same cache): cache should
        // satisfy every object fetch. Version still hits the API
        // because version metadata is mutable on the server.
        var pipeline2 = NewPipeline(env, api);
        var second = await pipeline2.ReceiveAsync(projectId, versionId, default);
        Assert.NotNull(second);
        Assert.Equal(expectedObjectCount, api.ObjectCalls); // unchanged
        Assert.Equal(2, api.VersionCalls);

        // Same scene topology either way.
        Assert.Equal(first.CountObjects(), second.CountObjects());
        Assert.Equal(first.CountMeshes(), second.CountMeshes());
    }

    [Fact]
    public async Task Test2_DeepNestedCollection_RoundTrips()
    {
        using var env = new TestEnv();
        var (api, projectId, versionId) = BuildDeepScene(depth: 5, leafMeshes: 50);

        var pipeline = NewPipeline(env, api);
        var scene = await pipeline.ReceiveAsync(projectId, versionId, default);

        // Topology check: 5 nested Collections + 50 meshes.
        var collectionCount = CountCollections(scene.Root);
        Assert.Equal(5, collectionCount);
        Assert.Equal(50, scene.CountMeshes());

        // Every leaf is a StagedMesh (no fallbacks, no unknowns).
        Assert.Empty(scene.Unknowns);

        // Layer paths preserve the hierarchy.
        var firstMesh = ScanMeshes(scene.Root).First();
        var segments = firstMesh.LayerPath.Split("::");
        Assert.Equal(5, segments.Length); // root + 4 nested
    }

    private static OrbitReceivePipeline NewPipeline(TestEnv env, FakeOrbitApi api) =>
        new(
            api,
            env.ContentCache,
            env.CreateBlobDownloader(api),
            env.CreateInMemoryUnknownSink(),
            env.Logger);

    // --------------------------------------------------------------
    // Synthetic scenes
    // --------------------------------------------------------------

    private static (FakeOrbitApi api, string projectId, string versionId, int objectCount) BuildSmallScene()
    {
        const string project = "p1";
        const string version = "v1";
        var api = new FakeOrbitApi();
        api.RegisterVersion(new VersionDescriptor(project, "m1", version, RootObjectId: "root"));

        // Root collection with two child meshes.
        api.RegisterObject("mesh-a", TestEnv.MakeMesh("mesh-a",
            vertices: new double[] { 0, 0, 0,  1, 0, 0,  0, 1, 0 },
            faces: new[] { 3, 0, 1, 2 }));
        api.RegisterObject("mesh-b", TestEnv.MakeMesh("mesh-b",
            vertices: new double[] { 0, 0, 0,  1, 0, 0,  0, 1, 0 },
            faces: new[] { 3, 0, 1, 2 }));
        api.RegisterObject("root",
            TestEnv.MakeCollection("root", "Root", new[] { "mesh-a", "mesh-b" }));

        return (api, project, version, objectCount: 3);
    }

    private static (FakeOrbitApi api, string projectId, string versionId) BuildDeepScene(
        int depth, int leafMeshes)
    {
        const string project = "p1";
        const string version = "v1";
        var api = new FakeOrbitApi();
        api.RegisterVersion(new VersionDescriptor(project, "m1", version, RootObjectId: "L0"));

        // Each level holds exactly one collection child (so the depth
        // of the path is `depth`); the deepest level holds all
        // leafMeshes meshes as siblings.
        for (int d = 0; d < depth; d++)
        {
            var name = $"L{d}";
            var children = d == depth - 1
                ? Enumerable.Range(0, leafMeshes).Select(i => $"m{i}").ToArray()
                : new[] { $"L{d + 1}" };
            api.RegisterObject(name, TestEnv.MakeCollection(name, name, children));
        }

        for (int i = 0; i < leafMeshes; i++)
        {
            var id = $"m{i}";
            api.RegisterObject(id, TestEnv.MakeMesh(id,
                vertices: new double[] { i, 0, 0,  i + 1, 0, 0,  i, 1, 0 },
                faces: new[] { 3, 0, 1, 2 }));
        }

        return (api, project, version);
    }

    private static int CountCollections(StagedNode node)
    {
        if (node is not StagedCollection coll) return 0;
        var count = 1;
        foreach (var child in coll.Children) count += CountCollections(child);
        return count;
    }

    private static IEnumerable<StagedMesh> ScanMeshes(StagedNode node)
    {
        switch (node)
        {
            case StagedMesh m: yield return m; yield break;
            case StagedCollection c:
                foreach (var ch in c.Children)
                    foreach (var nested in ScanMeshes(ch)) yield return nested;
                break;
        }
    }
}
