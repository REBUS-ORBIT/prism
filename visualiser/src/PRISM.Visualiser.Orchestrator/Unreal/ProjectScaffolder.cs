using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Serilog;

namespace PRISM.Visualiser.Orchestrator.Unreal;

/// <summary>
/// Per-run UE project scaffolder. Given a cached template zip + a runId,
/// extracts the template into
/// <c>%LOCALAPPDATA%\PRISM.Visualiser\runs\&lt;runId&gt;\REBUSVis\</c>,
/// then customises the project for this specific import:
///
/// <list type="bullet">
///   <item><description>
///     Rewrites <c>REBUSVis.uproject</c>'s <c>Description</c> field with
///     the runId so any artist who attaches the editor mid-import sees
///     which run it belongs to.
///   </description></item>
///   <item><description>
///     Rewrites <c>Config\DefaultEngine.ini</c>'s
///     <c>[/Script/EngineSettings.GameMapsSettings]</c>::<c>GameDefaultMap</c>
///     entry to <c>/Game/REBUS/Maps/Imported_&lt;runId&gt;.Imported_&lt;runId&gt;</c>
///     so opening the project boots straight into the soon-to-be-imported level.
///   </description></item>
///   <item><description>
///     Renders <c>import_orbit.py.in</c> (placeholder template) into the
///     scaffolded project at <c>Content\Python\import_orbit.py</c>,
///     substituting <c>{{RUN_ID}}</c> / <c>{{GLTF_PATH}}</c> /
///     <c>{{TARGET_FOLDER}}</c> / <c>{{LEVEL_NAME}}</c>.
///   </description></item>
/// </list>
///
/// <para>
/// The scaffolder is idempotent: a re-scaffold of the same runId blows
/// away the existing project directory first. This matches the per-run
/// project-copy model — a re-run is allowed to overwrite.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProjectScaffolder
{
    /// <summary>Project name baked into the template's .uproject filename.</summary>
    public const string ProjectFileName = "REBUSVis.uproject";

    /// <summary>Template descriptor key we rewrite per run.</summary>
    public const string DescriptionField = "Description";

    /// <summary>INI section + key the level redirect lives in.</summary>
    public const string GameMapsSection = "/Script/EngineSettings.GameMapsSettings";

    /// <summary>INI key for the default level path.</summary>
    public const string GameDefaultMapKey = "GameDefaultMap";

    /// <summary>UE content folder for our imports (mirrors BUILD.md §7).</summary>
    public const string ContentSubFolder = @"Content\REBUS";

    /// <summary>UE Python script destination inside the project.</summary>
    public const string PythonRelativePath = @"Content\Python\import_orbit.py";

    /// <summary>Source name (in the orchestrator's exe folder) of the template python.</summary>
    public const string PythonTemplateAssetName = "import_orbit.py.in";

    /// <summary>Schema name stamped into the per-run scaffold manifest.</summary>
    public const string ManifestSchema = "prism-visualiser/scaffold/v1";

    private readonly ILogger _log;
    private readonly string _pythonTemplate;

    public ProjectScaffolder(ILogger log, string pythonTemplate)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonTemplate);
        _pythonTemplate = pythonTemplate;
    }

    /// <summary>
    /// Build the default scaffolder, which loads the python template
    /// from the orchestrator's exe directory. Tests pass the template
    /// bytes directly to <see cref="ProjectScaffolder(ILogger, string)"/>
    /// and skip this helper.
    /// </summary>
    public static ProjectScaffolder CreateDefault(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var exeDir = AppContext.BaseDirectory;
        var path = Path.Combine(exeDir, PythonTemplateAssetName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Python template not found at '{path}'. Expected the orchestrator " +
                $"build to copy '{PythonTemplateAssetName}' to its output directory " +
                $"(see csproj <Content Include='Unreal\\PythonScripts\\{PythonTemplateAssetName}'>).",
                path);
        }
        var template = File.ReadAllText(path);
        return new ProjectScaffolder(log, template);
    }

    /// <summary>
    /// Resolve the per-run project root under <c>%LOCALAPPDATA%</c>.
    /// Mirrors <see cref="Logging.StructuredLog.ResolveLogsDirectory"/>'s
    /// scheme so logs + project + cache all live under the same runs/ tree.
    /// </summary>
    public static string ResolveDefaultProjectRoot(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(local, "PRISM.Visualiser", "runs", runId, "REBUSVis");
    }

    /// <summary>
    /// Scaffold a fresh per-run UE project. Returns paths the launcher
    /// + python script need.
    /// </summary>
    /// <param name="cacheEntry">Template zip from <see cref="TemplateFetcher.FetchAsync"/>.</param>
    /// <param name="runId">Run UUID — appears in the project description, level name, manifest.</param>
    /// <param name="gltfPath">Absolute path to the staged glTF the python script will import.</param>
    /// <param name="projectRoot">
    ///   Optional override of the project root (tests inject a temp dir;
    ///   the CLI uses <see cref="ResolveDefaultProjectRoot"/>).
    /// </param>
    public ScaffoldResult Scaffold(
        TemplateCacheEntry cacheEntry,
        string runId,
        string gltfPath,
        string? projectRoot = null)
    {
        ArgumentNullException.ThrowIfNull(cacheEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gltfPath);

        projectRoot ??= ResolveDefaultProjectRoot(runId);

        // Idempotent: nuke any prior copy. Per-run dir is owned by us,
        // so this is safe.
        if (Directory.Exists(projectRoot))
        {
            _log.Information("scaffold: clearing existing project root path={ProjectRoot}", projectRoot);
            Directory.Delete(projectRoot, recursive: true);
        }
        Directory.CreateDirectory(projectRoot);

        ExtractZip(cacheEntry.ZipPath, projectRoot);

        var (uprojectPath, descriptionRewriteOk) = RewriteUproject(projectRoot, runId);
        var (iniPath, levelPath) = RewriteDefaultEngineIni(projectRoot, runId);
        var pythonPath = RenderPythonScript(projectRoot, runId, gltfPath, levelPath);

        WriteManifest(projectRoot, cacheEntry, runId, gltfPath, levelPath);

        _log.Information(
            "scaffold: project ready runId={RunId} root={Root} level={Level} python={Python}",
            runId, projectRoot, levelPath, pythonPath);

        return new ScaffoldResult(
            ProjectRoot: projectRoot,
            UprojectPath: uprojectPath,
            DefaultEngineIniPath: iniPath,
            PythonScriptPath: pythonPath,
            LevelPath: levelPath,
            DescriptionRewritten: descriptionRewriteOk);
    }

    // ----------------------------------------------------------------
    // Zip extraction
    // ----------------------------------------------------------------

    private void ExtractZip(string zipPath, string destinationRoot)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException(
                $"Template zip missing at '{zipPath}'. " +
                "Did TemplateFetcher.FetchAsync run successfully?", zipPath);
        }

        // GitHub's release zip contents are flat (the zip's top-level
        // entries ARE the project files — REBUSVis.uproject, Config/, etc.).
        // If a future release bundles everything under a single top-level
        // folder (e.g. orbit-ue-template-v0.1.0/), we'd need to flatten;
        // for v0.1.0-ue5.7-scaffold, the layout is already flat, so a
        // straight ExtractToDirectory works.
        ZipFile.ExtractToDirectory(zipPath, destinationRoot, overwriteFiles: true);

        // Defensive flatten: if the zip turns out to have wrapped its
        // contents in a single top-level folder, hoist them up. We
        // detect this by looking for REBUSVis.uproject — if it's NOT
        // at the project root, walk one level down to find it.
        var uprojectAtRoot = Path.Combine(destinationRoot, ProjectFileName);
        if (!File.Exists(uprojectAtRoot))
        {
            var candidate = Directory
                .EnumerateDirectories(destinationRoot)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, ProjectFileName)));
            if (candidate is not null)
            {
                _log.Information("scaffold: flattening single top-level dir={Dir}", candidate);
                FlattenIntoParent(candidate, destinationRoot);
            }
        }

        if (!File.Exists(uprojectAtRoot))
        {
            throw new InvalidOperationException(
                $"Template zip '{zipPath}' did not contain '{ProjectFileName}' " +
                $"after extraction to '{destinationRoot}'. " +
                "Phase D's release format must be flat-rooted.");
        }
    }

    private static void FlattenIntoParent(string source, string parent)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(path);
            var target = Path.Combine(parent, name);
            if (Directory.Exists(path))
            {
                Directory.Move(path, target);
            }
            else
            {
                File.Move(path, target, overwrite: true);
            }
        }
        Directory.Delete(source);
    }

    // ----------------------------------------------------------------
    // .uproject rewrite
    // ----------------------------------------------------------------

    private (string Path, bool Rewritten) RewriteUproject(string projectRoot, string runId)
    {
        var uprojectPath = Path.Combine(projectRoot, ProjectFileName);
        var json = File.ReadAllText(uprojectPath);
        var node = JsonNode.Parse(json,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject
            ?? throw new InvalidOperationException(
                $"'{ProjectFileName}' is not a JSON object.");

        var desc = string.Format(CultureInfo.InvariantCulture,
            "PRISM Visualiser run {0} (auto-scaffolded)", runId);
        node[DescriptionField] = desc;

        var serialized = node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(uprojectPath, serialized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (uprojectPath, true);
    }

    // ----------------------------------------------------------------
    // DefaultEngine.ini rewrite
    // ----------------------------------------------------------------

    /// <summary>
    /// Update <c>DefaultEngine.ini</c>'s <c>GameDefaultMap</c> to point
    /// at the per-run level, returning the new ini path + the
    /// fully-qualified UE level path the python script will use.
    /// </summary>
    private (string IniPath, string LevelPath) RewriteDefaultEngineIni(string projectRoot, string runId)
    {
        var iniPath = Path.Combine(projectRoot, "Config", "DefaultEngine.ini");
        var levelName = "Imported_" + SanitiseRunId(runId);
        var levelPath = string.Format(CultureInfo.InvariantCulture,
            "/Game/REBUS/Maps/{0}.{0}", levelName);

        Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
        var existing = File.Exists(iniPath) ? File.ReadAllText(iniPath) : string.Empty;

        var rewritten = ReplaceOrAppendIniValue(
            existing, GameMapsSection, GameDefaultMapKey, levelPath);

        File.WriteAllText(iniPath, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return (iniPath, levelPath);
    }

    /// <summary>
    /// Replace the value of <paramref name="key"/> in
    /// <paramref name="section"/> with <paramref name="value"/>; insert
    /// the section + key if missing. Returns the rewritten ini text.
    /// Public to be unit-testable in isolation.
    /// </summary>
    public static string ReplaceOrAppendIniValue(
        string ini, string section, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(ini);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var newline = DetectNewline(ini);
        var lines = ini.Length == 0
            ? new List<string>()
            : ini.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();

        var sectionHeader = "[" + section + "]";
        int sectionIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), sectionHeader, StringComparison.Ordinal))
            {
                sectionIdx = i;
                break;
            }
        }

        if (sectionIdx < 0)
        {
            // Append the section + key/value at the end. Make sure
            // there's a blank line before the new section unless the
            // file is empty / already ends with a blank line.
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }
            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
            return string.Join(newline, lines) + newline;
        }

        // Walk the section's body, replacing existing key or noting
        // where to insert it.
        int sectionEnd = lines.Count;
        for (int i = sectionIdx + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('[') && trimmed.TrimEnd().EndsWith(']'))
            {
                sectionEnd = i;
                break;
            }
        }

        for (int i = sectionIdx + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            var lineKey = trimmed[..eq].TrimEnd();
            if (string.Equals(lineKey, key, StringComparison.Ordinal))
            {
                lines[i] = $"{key}={value}";
                return string.Join(newline, lines)
                    + (ini.EndsWith('\n') ? newline : string.Empty);
            }
        }

        // Section exists but key not found — insert at the end of the section.
        lines.Insert(sectionEnd, $"{key}={value}");
        return string.Join(newline, lines)
            + (ini.EndsWith('\n') ? newline : string.Empty);
    }

    private static string DetectNewline(string s)
    {
        if (s.Contains("\r\n", StringComparison.Ordinal)) return "\r\n";
        if (s.Contains('\n', StringComparison.Ordinal)) return "\n";
        return Environment.NewLine;
    }

    // ----------------------------------------------------------------
    // Python script render
    // ----------------------------------------------------------------

    private string RenderPythonScript(
        string projectRoot, string runId, string gltfPath, string levelPath)
    {
        var rendered = Render(_pythonTemplate, runId, gltfPath, levelPath);

        var pythonPath = Path.Combine(projectRoot, PythonRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(pythonPath)!);
        File.WriteAllText(pythonPath, rendered, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return pythonPath;
    }

    /// <summary>
    /// Render the python template by replacing <c>{{...}}</c> placeholders.
    /// Public + static so tests can verify the substitutions in
    /// isolation without touching the file system.
    /// </summary>
    public static string Render(string template, string runId, string gltfPath, string levelPath)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gltfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(levelPath);

        var sanitised = SanitiseRunId(runId);
        var levelName = "Imported_" + sanitised;
        var targetFolder = "/Game/REBUS/Imported_" + sanitised;

        return template
            .Replace("{{RUN_ID}}", sanitised, StringComparison.Ordinal)
            .Replace("{{GLTF_PATH}}", EscapePythonStringContent(gltfPath), StringComparison.Ordinal)
            .Replace("{{TARGET_FOLDER}}", targetFolder, StringComparison.Ordinal)
            .Replace("{{LEVEL_NAME}}", levelName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sanitise a runId for use in UE asset names. UE refuses anything
    /// outside <c>[A-Za-z0-9_]</c> in asset paths; replace dashes (the
    /// common UUID separator) with underscores. Also clamps length to
    /// 64 chars to stay under UE's path budget.
    /// </summary>
    public static string SanitiseRunId(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var sb = new StringBuilder(Math.Min(runId.Length, 64));
        foreach (var c in runId)
        {
            if (sb.Length >= 64) break;
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }
        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? "run" : s;
    }

    /// <summary>
    /// The python template uses <c>r"{{GLTF_PATH}}"</c> — a raw string
    /// literal. Raw strings can't contain a closing <c>"</c> mid-token,
    /// but otherwise treat backslashes literally (which is what we want
    /// on Windows). Escape any embedded <c>"</c> by terminating the raw
    /// string and re-opening with an escape — pragmatic enough for the
    /// orchestrator's runId-derived stage paths, which never contain
    /// quotes in practice.
    /// </summary>
    private static string EscapePythonStringContent(string s) =>
        s.Replace("\"", "\\\"", StringComparison.Ordinal);

    // ----------------------------------------------------------------
    // Per-run scaffold manifest sidecar
    // ----------------------------------------------------------------

    private static void WriteManifest(
        string projectRoot,
        TemplateCacheEntry entry,
        string runId,
        string gltfPath,
        string levelPath)
    {
        var manifest = new JsonObject
        {
            ["schema"] = ManifestSchema,
            ["runId"] = runId,
            ["templateTag"] = entry.Tag,
            ["templateSha256"] = entry.Sha256,
            ["templateZip"] = entry.ZipPath,
            ["gltfPath"] = gltfPath,
            ["levelPath"] = levelPath,
            ["scaffoldedAt"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        var manifestPath = Path.Combine(projectRoot, "REBUSVis.scaffold.json");
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

/// <summary>Result of <see cref="ProjectScaffolder.Scaffold"/>.</summary>
public sealed record ScaffoldResult(
    string ProjectRoot,
    string UprojectPath,
    string DefaultEngineIniPath,
    string PythonScriptPath,
    string LevelPath,
    bool DescriptionRewritten);
