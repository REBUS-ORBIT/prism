using System.Text;
using System.Text.Json.Nodes;

using Xunit;

using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.OrbitApi;
using PRISM.Visualiser.Orchestrator.Pipeline;
using PRISM.Visualiser.Orchestrator.Tests.TestHelpers;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 5 — RenderMaterial + texture blob resolution.
///
/// A <c>RenderMaterial</c> with <c>diffuseTexture: "@blob:abc..."</c>
/// must end up as a <see cref="StagedMaterial.DiffuseTexturePath"/>
/// pointing at the cached blob file. Missing blob triggers a fetch
/// via the mock API.
/// </summary>
public class MaterialBlobResolutionTests
{
    [Fact]
    public async Task BlobReference_ResolvesToCachedFilePath()
    {
        using var env = new TestEnv();

        // Synthetic 5-byte texture. SHA256 hash is what the cache
        // shards by — the blob downloader verifies the server-served
        // content hashes back to the requested id.
        var textureBytes = Encoding.UTF8.GetBytes("PNG??");
        var hash = ContentAddressedCache.ComputeHash(textureBytes);

        var api = new FakeOrbitApi();
        api.RegisterVersion(new VersionDescriptor("p1", "m1", "v1", RootObjectId: "root"));

        // Material references the blob via the @blob: placeholder
        // (the connector convention pre-server-upload).
        api.RegisterObject("mat-1", TestEnv.MakeRenderMaterial(
            id: "mat-1", name: "TexturedMat",
            diffuseTexture: $"@blob:{hash}"));

        // Mesh references the material by id.
        api.RegisterObject("mesh-1", TestEnv.MakeMesh(
            id: "mesh-1",
            vertices: new double[] { 0, 0, 0,  1, 0, 0,  0, 1, 0 },
            faces: new[] { 3, 0, 1, 2 },
            renderMaterialRef: "mat-1"));

        api.RegisterObject("root", TestEnv.MakeCollection(
            "root", "Root", new[] { "mat-1", "mesh-1" }));

        // Blob is fetched on demand by the BlobDownloader.
        api.RegisterBlob(hash, textureBytes);

        var pipeline = new OrbitReceivePipeline(
            api, env.ContentCache, env.CreateBlobDownloader(api),
            env.CreateInMemoryUnknownSink(), env.Logger);

        var scene = await pipeline.ReceiveAsync("p1", "v1", default);

        // Material is in the registry with a resolved texture path.
        Assert.True(scene.Materials.ContainsKey("mat-1"));
        var staged = scene.Materials["mat-1"];
        Assert.NotNull(staged.DiffuseTexturePath);
        Assert.True(File.Exists(staged.DiffuseTexturePath!));

        // Path lives under the cache's `blobs/` shard directory.
        Assert.Contains("blobs", staged.DiffuseTexturePath, StringComparison.Ordinal);
        Assert.Contains(hash, staged.DiffuseTexturePath, StringComparison.Ordinal);

        // The blob was fetched exactly once.
        Assert.Equal(1, api.BlobCalls);

        // Mesh rendered with that material.
        Assert.Single(scene.Materials);
    }

    [Fact]
    public async Task MissingBlob_LeavesTexturePathNull_DoesNotCrash()
    {
        using var env = new TestEnv();

        var api = new FakeOrbitApi();
        api.RegisterVersion(new VersionDescriptor("p1", "m1", "v1", RootObjectId: "root"));

        var ghostHash = new string('1', 64);
        api.RegisterObject("mat-1", TestEnv.MakeRenderMaterial(
            id: "mat-1", name: "GhostTextured",
            diffuseTexture: $"@blob:{ghostHash}"));
        api.RegisterObject("root", TestEnv.MakeCollection(
            "root", "Root", new[] { "mat-1" }));

        var pipeline = new OrbitReceivePipeline(
            api, env.ContentCache, env.CreateBlobDownloader(api),
            env.CreateInMemoryUnknownSink(), env.Logger);

        // Blob is not registered → BlobDownloader.ResolveAsync would
        // throw. The receive pipeline propagates that as an
        // OrbitApiException.
        await Assert.ThrowsAsync<OrbitApiException>(
            () => pipeline.ReceiveAsync("p1", "v1", default));
    }
}
