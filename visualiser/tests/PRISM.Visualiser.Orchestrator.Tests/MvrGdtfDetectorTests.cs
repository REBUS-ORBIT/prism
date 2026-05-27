using System.Text.Json.Nodes;

using Xunit;

using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Phase J — covers the four detection paths documented in the plan:
///   1. Speckle MVR object in the staged scene tree.
///   2. Project-level MVR attachment staged on disk.
///   3. Neither source has anything (mesh-only scene; no attachments dir).
///   4. Both sources contribute simultaneously.
///
/// Each test builds an isolated temp stage dir so the on-disk scan
/// branch can be exercised without polluting the real visualiser cache.
/// </summary>
public class MvrGdtfDetectorTests : IDisposable
{
    private readonly string _stageRoot;

    public MvrGdtfDetectorTests()
    {
        _stageRoot = Path.Combine(
            Path.GetTempPath(),
            "prism-mvr-detector-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_stageRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stageRoot)) Directory.Delete(_stageRoot, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Test1_Detector_Finds_Speckle_Mvr_Object_In_StagedScene()
    {
        // Stage the "actual" MVR file the displayValue will point at, so the
        // detector's path-normalisation step has something real to resolve.
        var mvrFile = Path.Combine(_stageRoot, "blobs", "abc.mvr");
        Directory.CreateDirectory(Path.GetDirectoryName(mvrFile)!);
        File.WriteAllBytes(mvrFile, new byte[] { 0x50, 0x4B }); // PK header (MVR is a zip)

        var rawJson = new JsonObject
        {
            ["id"] = "mvr-1",
            ["speckle_type"] = "Orbit.Objects.Lighting.MvrScene",
            ["displayValue"] = mvrFile,
        }.ToJsonString();

        var mvrNode = new StagedUnknown(
            SourceObjectId: "mvr-1",
            SpeckleType: "Orbit.Objects.Lighting.MvrScene",
            LayerPath: "Lighting",
            RawJson: rawJson);

        var root = new StagedCollection(
            SourceObjectId: "root",
            SpeckleType: "Speckle.Core.Models.Collections.Collection",
            Name: "root",
            LayerPath: string.Empty,
            Children: new StagedNode[] { mvrNode });

        var scene = new StagedScene(
            new VersionDescriptor("p1", "m1", "v1", RootObjectId: "root"),
            root,
            new Dictionary<string, StagedMaterial>(),
            Array.Empty<StagedUnknown>());

        var detector = new MvrGdtfDetector();
        var result = detector.Detect(scene, _stageRoot);

        Assert.True(result.HasAny, "Detector should report HasAny=true when a Speckle MVR object is staged.");
        Assert.Single(result.MvrPaths);
        Assert.Empty(result.GdtfPaths);
        // Path round-trips through Path.GetFullPath — compare via the
        // same normalisation rather than string equality on the raw input.
        Assert.Equal(Path.GetFullPath(mvrFile), result.MvrPaths[0]);
    }

    [Fact]
    public void Test2_Detector_Finds_Mvr_File_By_Extension_In_Attachments_Dir()
    {
        var attachmentsDir = Path.Combine(_stageRoot, MvrGdtfDetector.AttachmentsSubFolder);
        Directory.CreateDirectory(attachmentsDir);
        var mvrPath = Path.Combine(attachmentsDir, "lighting.mvr");
        File.WriteAllBytes(mvrPath, new byte[] { 0x50, 0x4B });

        // Empty scene — only the filesystem source contributes.
        var scene = EmptyMeshOnlyScene();

        var detector = new MvrGdtfDetector();
        var result = detector.Detect(scene, _stageRoot);

        Assert.True(result.HasAny);
        Assert.Single(result.MvrPaths);
        Assert.Equal(Path.GetFullPath(mvrPath), result.MvrPaths[0]);
        Assert.Empty(result.GdtfPaths);
    }

    [Fact]
    public void Test3_Detector_Returns_Empty_When_Nothing_Present()
    {
        // Mesh-only scene; no attachments directory exists at all.
        var meshOnlyRoot = new StagedCollection(
            SourceObjectId: "root",
            SpeckleType: "Speckle.Core.Models.Collections.Collection",
            Name: "root",
            LayerPath: string.Empty,
            Children: new StagedNode[]
            {
                new StagedMesh(
                    SourceObjectId: "mesh-1",
                    SpeckleType: "Objects.Geometry.Mesh",
                    LayerPath: "Walls",
                    Vertices: Array.Empty<System.Numerics.Vector3>(),
                    Indices:  Array.Empty<int>(),
                    Normals:  null,
                    TexCoords: null,
                    Colors:   null,
                    MaterialId: null),
            });
        var scene = new StagedScene(
            new VersionDescriptor("p1", "m1", "v1", RootObjectId: "root"),
            meshOnlyRoot,
            new Dictionary<string, StagedMaterial>(),
            Array.Empty<StagedUnknown>());

        var detector = new MvrGdtfDetector();
        var result = detector.Detect(scene, _stageRoot);

        Assert.False(result.HasAny);
        Assert.Empty(result.MvrPaths);
        Assert.Empty(result.GdtfPaths);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void Test4_Detector_Combines_Both_Sources()
    {
        // Source 1: Speckle MVR object in the staged scene.
        var mvrFileFromScene = Path.Combine(_stageRoot, "blobs", "scene-mvr.mvr");
        Directory.CreateDirectory(Path.GetDirectoryName(mvrFileFromScene)!);
        File.WriteAllBytes(mvrFileFromScene, new byte[] { 0x50, 0x4B });

        var mvrFromSpeckle = new StagedUnknown(
            SourceObjectId: "mvr-spkl",
            SpeckleType: "Orbit.Objects.Lighting.MvrScene",
            LayerPath: "Lighting",
            RawJson: new JsonObject
            {
                ["id"] = "mvr-spkl",
                ["speckle_type"] = "Orbit.Objects.Lighting.MvrScene",
                // Use blobPath here to also exercise the alternate field name.
                ["blobPath"] = mvrFileFromScene,
            }.ToJsonString());

        var root = new StagedCollection(
            SourceObjectId: "root",
            SpeckleType: "Speckle.Core.Models.Collections.Collection",
            Name: "root",
            LayerPath: string.Empty,
            Children: new StagedNode[] { mvrFromSpeckle });
        var scene = new StagedScene(
            new VersionDescriptor("p1", "m1", "v1", RootObjectId: "root"),
            root,
            new Dictionary<string, StagedMaterial>(),
            Array.Empty<StagedUnknown>());

        // Source 2: GDTF file in the attachments dir on disk.
        var attachmentsDir = Path.Combine(_stageRoot, MvrGdtfDetector.AttachmentsSubFolder);
        Directory.CreateDirectory(attachmentsDir);
        var gdtfPath = Path.Combine(attachmentsDir, "robe-spot.gdtf");
        File.WriteAllBytes(gdtfPath, new byte[] { 0x50, 0x4B });

        var detector = new MvrGdtfDetector();
        var result = detector.Detect(scene, _stageRoot);

        Assert.True(result.HasAny);
        Assert.Single(result.MvrPaths);
        Assert.Single(result.GdtfPaths);
        Assert.Equal(Path.GetFullPath(mvrFileFromScene), result.MvrPaths[0]);
        Assert.Equal(Path.GetFullPath(gdtfPath), result.GdtfPaths[0]);
        Assert.Equal(2, result.TotalCount);
    }

    // ----------------------------------------------------------------
    // Bonus coverage: render-template substitution + path extraction
    // helpers used by LaunchMvrImportAsync. These don't appear in the
    // 4-test spec but exercise the launcher's pure-function surface
    // without spawning UE.
    // ----------------------------------------------------------------

    [Fact]
    public void RenderMvrTemplate_Substitutes_All_Placeholders()
    {
        const string template = "RUN_ID={{RUN_ID}}|MVR={{MVR_PATHS_JSON}}|GDTF={{GDTF_PATHS_JSON}}|FOLDER={{TARGET_FOLDER}}|LEVEL={{LEVEL_NAME}}";
        var rendered = UnrealLauncher.RenderMvrTemplate(
            template,
            runId: "abc123",
            mvrPaths: new[] { @"C:\stage\lighting.mvr" },
            gdtfPaths: new[] { @"C:\stage\robe.gdtf", @"C:\stage\martin.gdtf" },
            targetFolder: "/Game/REBUS/Imported_abc123/Lighting",
            levelName: "Imported_abc123");

        Assert.Contains("RUN_ID=abc123", rendered, StringComparison.Ordinal);
        Assert.Contains("FOLDER=/Game/REBUS/Imported_abc123/Lighting", rendered, StringComparison.Ordinal);
        Assert.Contains("LEVEL=Imported_abc123", rendered, StringComparison.Ordinal);
        // JSON-encoded path arrays should round-trip via json.loads.
        Assert.Contains("MVR=[", rendered, StringComparison.Ordinal);
        Assert.Contains("GDTF=[", rendered, StringComparison.Ordinal);
        Assert.Contains("lighting.mvr", rendered, StringComparison.Ordinal);
        Assert.Contains("robe.gdtf", rendered, StringComparison.Ordinal);
        Assert.Contains("martin.gdtf", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMvrLine_Recognises_Ready_And_Error_Markers()
    {
        var ready = UnrealLauncher.ParseMvrLine(
            "PRISM_VISUALISER_MVR_READY {\"runId\":\"r1\",\"gdtfCount\":2,\"mvrCount\":1,\"importDurationMs\":4200}");
        Assert.Equal(MarkerKind.Ready, ready.Kind);
        Assert.NotNull(ready.ReadyMarker);
        Assert.Equal(2, ready.ReadyMarker!.GdtfCount);
        Assert.Equal(1, ready.ReadyMarker.MvrCount);

        var error = UnrealLauncher.ParseMvrLine(
            "PRISM_VISUALISER_MVR_ERROR {\"code\":\"mvr_import_failed\",\"message\":\"plugin disabled\"}");
        Assert.Equal(MarkerKind.Error, error.Kind);
        Assert.NotNull(error.ErrorMarker);
        Assert.Equal("mvr_import_failed", error.ErrorMarker!.Code);

        var noise = UnrealLauncher.ParseMvrLine(
            "LogTemp: just another log line, not a marker.");
        Assert.Equal(MarkerKind.None, noise.Kind);
    }

    private static StagedScene EmptyMeshOnlyScene()
    {
        var root = new StagedCollection(
            SourceObjectId: "root",
            SpeckleType: "Speckle.Core.Models.Collections.Collection",
            Name: "root",
            LayerPath: string.Empty,
            Children: Array.Empty<StagedNode>());
        return new StagedScene(
            new VersionDescriptor("p1", "m1", "v1", RootObjectId: "root"),
            root,
            new Dictionary<string, StagedMaterial>(),
            Array.Empty<StagedUnknown>());
    }
}
