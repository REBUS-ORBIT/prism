using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;

using Xunit;

using PRISM.Visualiser.Orchestrator.Tests.TestHelpers;
using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 9 — <see cref="ProjectScaffolder"/> end-to-end on a fake
/// template zip. Verifies:
///
/// <list type="bullet">
///   <item><description>The per-run project tree is created.</description></item>
///   <item><description>
///     <c>REBUSVis.uproject</c>'s <c>Description</c> field is rewritten
///     with the runId.
///   </description></item>
///   <item><description>
///     <c>Config\DefaultEngine.ini</c> parses cleanly and contains the
///     run-specific <c>GameDefaultMap</c>.
///   </description></item>
///   <item><description>
///     The python script is rendered with placeholders substituted.
///   </description></item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public class ProjectScaffolderTests
{
    [Fact]
    public void Scaffold_ExtractsTemplate_RewritesUproject_Ini_AndPython()
    {
        using var env = new TestEnv();

        // Fake template: a flat-rooted zip containing the minimum
        // surface ProjectScaffolder rewrites.
        var zipPath = BuildFakeTemplateZip(env.TempRoot, includeTopLevelFolder: false);
        var sha = SHA256OfFile(zipPath);
        File.WriteAllText(zipPath + ".sha256", sha);
        var entry = new TemplateCacheEntry(
            Tag: "v0.1.0-ue5.7-scaffold", ZipPath: zipPath, Sha256: sha, FromCache: false);

        var pythonTemplate = "RUN_ID = \"{{RUN_ID}}\"\n" +
                             "GLTF_PATH = r\"{{GLTF_PATH}}\"\n" +
                             "TARGET_FOLDER = \"{{TARGET_FOLDER}}\"\n" +
                             "LEVEL_NAME = \"{{LEVEL_NAME}}\"\n";
        var scaffolder = new ProjectScaffolder(env.Logger, pythonTemplate);
        var projectRoot = Path.Combine(env.TempRoot, "scaffold-out");
        var runId = "abc-123-DEAD-beef";
        var gltfPath = Path.Combine(env.TempRoot, "stage", runId, "scene.gltf");
        Directory.CreateDirectory(Path.GetDirectoryName(gltfPath)!);
        File.WriteAllText(gltfPath, "{}");

        var result = scaffolder.Scaffold(entry, runId, gltfPath, projectRoot);

        // 1. .uproject Description is rewritten.
        Assert.True(File.Exists(result.UprojectPath));
        var uproject = JsonNode.Parse(File.ReadAllText(result.UprojectPath))!.AsObject();
        Assert.Contains(runId, uproject["Description"]!.GetValue<string>(),
            StringComparison.Ordinal);

        // 2. DefaultEngine.ini is rewritten and parses cleanly.
        Assert.True(File.Exists(result.DefaultEngineIniPath));
        var iniText = File.ReadAllText(result.DefaultEngineIniPath);
        var sanitised = ProjectScaffolder.SanitiseRunId(runId);
        Assert.Contains($"[{ProjectScaffolder.GameMapsSection}]", iniText, StringComparison.Ordinal);
        Assert.Contains($"GameDefaultMap=/Game/REBUS/Maps/Imported_{sanitised}.Imported_{sanitised}",
            iniText, StringComparison.Ordinal);
        AssertIniRoundTrips(iniText, ProjectScaffolder.GameMapsSection,
            ProjectScaffolder.GameDefaultMapKey);

        // 3. Python script is rendered with the placeholders replaced.
        Assert.True(File.Exists(result.PythonScriptPath));
        var py = File.ReadAllText(result.PythonScriptPath);
        Assert.DoesNotContain("{{RUN_ID}}", py, StringComparison.Ordinal);
        Assert.DoesNotContain("{{GLTF_PATH}}", py, StringComparison.Ordinal);
        Assert.DoesNotContain("{{TARGET_FOLDER}}", py, StringComparison.Ordinal);
        Assert.DoesNotContain("{{LEVEL_NAME}}", py, StringComparison.Ordinal);
        Assert.Contains(sanitised, py, StringComparison.Ordinal);
        Assert.Contains(gltfPath, py, StringComparison.Ordinal);
        Assert.Contains($"Imported_{sanitised}", py, StringComparison.Ordinal);

        // 4. Per-run scaffold manifest sidecar is written.
        var manifestPath = Path.Combine(result.ProjectRoot, "REBUSVis.scaffold.json");
        Assert.True(File.Exists(manifestPath));
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        Assert.Equal(ProjectScaffolder.ManifestSchema, manifest["schema"]!.GetValue<string>());
        Assert.Equal(runId, manifest["runId"]!.GetValue<string>());
        Assert.Equal(sha, manifest["templateSha256"]!.GetValue<string>());

        // 5. Re-run is idempotent: scaffolding the same runId again
        //    blows away + recreates the project root, no exceptions.
        var second = scaffolder.Scaffold(entry, runId, gltfPath, projectRoot);
        Assert.Equal(result.LevelPath, second.LevelPath);
        Assert.Equal(result.UprojectPath, second.UprojectPath);
    }

    [Fact]
    public void Scaffold_FlattensTopLevelDirectory_WhenZipIsNested()
    {
        using var env = new TestEnv();
        var zipPath = BuildFakeTemplateZip(env.TempRoot, includeTopLevelFolder: true);
        var sha = SHA256OfFile(zipPath);
        var entry = new TemplateCacheEntry(
            Tag: "vNested", ZipPath: zipPath, Sha256: sha, FromCache: false);

        var scaffolder = new ProjectScaffolder(env.Logger, "RUN={{RUN_ID}}\n");
        var result = scaffolder.Scaffold(
            entry, "run-1", Path.Combine(env.TempRoot, "scene.gltf"),
            Path.Combine(env.TempRoot, "scaffold-nested"));

        // The scaffolder must hoist REBUSVis.uproject up to the project
        // root regardless of whether the zip was flat or nested.
        Assert.True(File.Exists(result.UprojectPath));
        Assert.Equal(Path.GetDirectoryName(result.UprojectPath), result.ProjectRoot);
    }

    [Fact]
    public void Render_SubstitutesAllFourPlaceholders()
    {
        const string template =
            "ID={{RUN_ID}}\nGLTF=r\"{{GLTF_PATH}}\"\nFOLDER={{TARGET_FOLDER}}\nLEVEL={{LEVEL_NAME}}\n";
        var rendered = ProjectScaffolder.Render(
            template,
            runId: "abc-DEF-123",
            gltfPath: @"C:\stage\scene.gltf",
            levelPath: "/Game/REBUS/Maps/Imported_abc_DEF_123.Imported_abc_DEF_123");

        Assert.DoesNotContain("{{", rendered, StringComparison.Ordinal);
        Assert.Contains("ID=abc_DEF_123", rendered, StringComparison.Ordinal);
        Assert.Contains(@"GLTF=r""C:\stage\scene.gltf""", rendered, StringComparison.Ordinal);
        Assert.Contains("FOLDER=/Game/REBUS/Imported_abc_DEF_123", rendered, StringComparison.Ordinal);
        Assert.Contains("LEVEL=Imported_abc_DEF_123", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceOrAppendIniValue_AppendsSection_WhenMissing()
    {
        const string ini = "[Other]\nFoo=bar\n";
        var result = ProjectScaffolder.ReplaceOrAppendIniValue(
            ini, "/Script/EngineSettings.GameMapsSettings",
            "GameDefaultMap", "/Game/REBUS/Maps/X.X");
        Assert.Contains("[/Script/EngineSettings.GameMapsSettings]", result, StringComparison.Ordinal);
        Assert.Contains("GameDefaultMap=/Game/REBUS/Maps/X.X", result, StringComparison.Ordinal);
        Assert.Contains("[Other]", result, StringComparison.Ordinal);
        Assert.Contains("Foo=bar", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceOrAppendIniValue_ReplacesExistingValue_InPlace()
    {
        const string ini =
            "[/Script/EngineSettings.GameMapsSettings]\n" +
            "GameDefaultMap=/Game/Old.Old\n" +
            "ServerDefaultMap=/Game/Server.Server\n";

        var result = ProjectScaffolder.ReplaceOrAppendIniValue(
            ini, "/Script/EngineSettings.GameMapsSettings",
            "GameDefaultMap", "/Game/New.New");

        Assert.DoesNotContain("/Game/Old.Old", result, StringComparison.Ordinal);
        Assert.Contains("GameDefaultMap=/Game/New.New", result, StringComparison.Ordinal);
        Assert.Contains("ServerDefaultMap=/Game/Server.Server", result, StringComparison.Ordinal);
    }

    private static string BuildFakeTemplateZip(string tempRoot, bool includeTopLevelFolder)
    {
        var srcDir = Path.Combine(tempRoot, "fake-template-src" + (includeTopLevelFolder ? "-nested" : ""));
        var contentRoot = includeTopLevelFolder
            ? Path.Combine(srcDir, "orbit-ue-template-vN")
            : srcDir;
        Directory.CreateDirectory(Path.Combine(contentRoot, "Config"));
        Directory.CreateDirectory(Path.Combine(contentRoot, "Content", "REBUS", "Maps"));
        Directory.CreateDirectory(Path.Combine(contentRoot, "Content", "Python"));

        // Minimal .uproject — just enough that JsonNode.Parse round-trips.
        var uproject = new JsonObject
        {
            ["FileVersion"] = 3,
            ["EngineAssociation"] = "5.7",
            ["Description"] = "scaffold placeholder",
            ["Plugins"] = new JsonArray(),
        };
        File.WriteAllText(
            Path.Combine(contentRoot, "REBUSVis.uproject"),
            uproject.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        // Minimal DefaultEngine.ini with a different section so the
        // scaffolder has to either replace or append the GameMaps key.
        File.WriteAllText(
            Path.Combine(contentRoot, "Config", "DefaultEngine.ini"),
            "[/Script/Engine.RendererSettings]\nr.DefaultFeature.AutoExposure=False\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var zipPath = Path.Combine(tempRoot, "fake-template" +
            (includeTopLevelFolder ? "-nested" : "") + ".zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(srcDir, zipPath);
        return zipPath;
    }

    private static string SHA256OfFile(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static void AssertIniRoundTrips(string iniText, string section, string key)
    {
        // Tiny in-line ini parser — the round-trip assertion is "we
        // can find <section>::<key> by re-reading our own output".
        var lines = iniText.Split('\n');
        bool inSection = false;
        bool keyFound = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = string.Equals(line, "[" + section + "]", StringComparison.Ordinal);
                continue;
            }
            if (!inSection) continue;
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            if (string.Equals(line[..eq].TrimEnd(), key, StringComparison.Ordinal))
            {
                keyFound = true;
                break;
            }
        }
        Assert.True(keyFound,
            $"Rewritten ini did not contain [{section}]::{key} on round-trip parse.");
    }
}
