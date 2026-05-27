using System.Text.Json.Nodes;

using Serilog;

using PRISM.Visualiser.Orchestrator.Cache;
using PRISM.Visualiser.Orchestrator.Converters.FromOrbit;
using PRISM.Visualiser.Orchestrator.OrbitApi;

namespace PRISM.Visualiser.Orchestrator.Tests.TestHelpers;

/// <summary>
/// Per-test ambient setup: a unique cache root under
/// <c>%TEMP%\prism-vis-test-&lt;guid&gt;</c>, a Serilog sink-less
/// logger, and helpers for the smoke tests' synthetic data shapes.
///
/// Disposable so xUnit cleans up the temp directory when the test
/// finishes (tracked + deleted on <see cref="Dispose"/>).
/// </summary>
public sealed class TestEnv : IDisposable
{
    public string TempRoot { get; }
    public CacheRoot Cache { get; }
    public ContentAddressedCache ContentCache { get; }
    public ILogger Logger { get; }

    public TestEnv()
    {
        TempRoot = Path.Combine(
            Path.GetTempPath(),
            "prism-vis-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(TempRoot);
        Cache = CacheRoot.ResolveAt(TempRoot).EnsureCreated();
        ContentCache = new ContentAddressedCache(Cache);
        Logger = Serilog.Core.Logger.None;
    }

    public string StageDir => Path.Combine(Cache.Stage, "test-run");

    public BlobDownloader CreateBlobDownloader(FakeOrbitApi api) =>
        new(api, ContentCache, Logger);

    public UnknownObjectSink CreateInMemoryUnknownSink() => new();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempRoot))
            {
                Directory.Delete(TempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup — a flaky AV scanner holding a file
            // open should not fail the test run.
        }
    }

    // --------------------------------------------------------------
    // Synthetic JSON builders
    // --------------------------------------------------------------

    public static JsonObject MakeCollection(string id, string name, IEnumerable<string>? childRefs = null)
    {
        var elements = new JsonArray();
        if (childRefs is not null)
        {
            foreach (var refId in childRefs)
            {
                elements.Add(new JsonObject
                {
                    ["referencedId"] = refId,
                    ["speckle_type"] = "reference",
                });
            }
        }
        return new JsonObject
        {
            ["id"] = id,
            ["speckle_type"] = "Speckle.Core.Models.Collections.Collection",
            ["collectionType"] = "layer",
            ["name"] = name,
            ["elements"] = elements,
        };
    }

    public static JsonObject MakeMesh(
        string id,
        IReadOnlyList<double> vertices,
        IReadOnlyList<int> faces,
        string? renderMaterialRef = null)
    {
        var v = new JsonArray();
        foreach (var d in vertices) v.Add(d);
        var f = new JsonArray();
        foreach (var i in faces) f.Add(i);

        var mesh = new JsonObject
        {
            ["id"] = id,
            ["speckle_type"] = "Objects.Geometry.Mesh",
            ["vertices"] = v,
            ["faces"] = f,
            ["units"] = "m",
        };
        if (renderMaterialRef is not null)
        {
            mesh["renderMaterial"] = new JsonObject
            {
                ["referencedId"] = renderMaterialRef,
                ["speckle_type"] = "reference",
            };
        }
        return mesh;
    }

    public static JsonObject MakeRenderMaterial(
        string id,
        string name = "default",
        long diffuse = 0xFFCCCCCCL,
        string? diffuseTexture = null)
    {
        var mat = new JsonObject
        {
            ["id"] = id,
            ["speckle_type"] = "Objects.Other.RenderMaterial",
            ["name"] = name,
            ["diffuse"] = diffuse,
            ["emissive"] = 0xFF000000L,
            ["opacity"] = 1.0,
            ["roughness"] = 0.5,
            ["metalness"] = 0.0,
        };
        if (diffuseTexture is not null)
        {
            mat["diffuseTexture"] = diffuseTexture;
        }
        return mat;
    }

    public static JsonObject MakeUnknown(string id, string speckleType) => new()
    {
        ["id"] = id,
        ["speckle_type"] = speckleType,
        ["payload"] = new JsonObject { ["arbitrary"] = "data" },
    };
}
