using Microsoft.Extensions.Logging;
using Rhino.Runtime.InProcess;

namespace PRISM.Agent.Rhino;

/// <summary>
/// Process-wide Rhino host wrapper.
///
/// Owns the single <see cref="RhinoCore"/> instance that starts the Rhino 8
/// engine inside this process.  Must be constructed AFTER
/// <see cref="RhinoInside.Resolver.Initialize"/> has been called so that
/// managed + native Rhino assemblies can be resolved.
///
/// Per-job worker slots call <see cref="CreateDoc"/> to get a fresh headless
/// document; all serialise through the single Rhino engine (Rhino is not
/// re-entrant).
/// </summary>
public sealed class RhinoHost : IDisposable
{
    readonly ILogger<RhinoHost> _log;
    readonly RhinoCore _core;
    bool _disposed;

    public RhinoHost(ILogger<RhinoHost> log, string? rhinoSystemDir = null)
    {
        _log = log;

        // Prepend the Rhino system directory to PATH so that the Windows DLL
        // loader finds native Rhino DLLs via the default search order.  This
        // complements the AddDllDirectory call made by Resolver.Initialize and
        // ensures DllImport attributes that use LoadLibrary (not LoadLibraryEx
        // with LOAD_LIBRARY_SEARCH_USER_DIRS) also succeed.
        if (!string.IsNullOrEmpty(rhinoSystemDir))
            PrependToPath(rhinoSystemDir);

        _log.LogInformation("RhinoHost: starting Rhino.Inside core{Suffix}",
            rhinoSystemDir is null ? "" : $" — system dir: {rhinoSystemDir}");

        // RhinoCore is the object that actually loads the native Rhino engine
        // DLLs and initialises the Rhino application object.  Without it,
        // any P/Invoke into the Rhino C++ layer throws DllNotFoundException.
        //
        // v0.1.20: drop /notemplate so Rhino loads its default template on
        // startup. That establishes a real ActiveDoc with full interactive
        // infrastructure — RDK hydration, render-mesh cache, doc.Bitmaps
        // table, doc.RenderMaterials, ChunkUnit etc — exactly the way the
        // working interactive ORBIT plug-in sees the world. Without a
        // default template, headless docs have no doc-level RDK and
        // `mat.RenderMaterial.FirstChild` is null even for clearly textured
        // materials.
        _core = new RhinoCore(new[] { "/nosplash" });

        _log.LogInformation("RhinoHost: Rhino {Version} ready", global::Rhino.RhinoApp.Version);

        // v0.1.20 verification: did the default template establish an ActiveDoc?
        try
        {
            var ad = global::Rhino.RhinoDoc.ActiveDoc;
            _log.LogInformation(
                "[ORBIT-DIAG] post-boot ActiveDoc={HasAd} RuntimeSerial={Sn}",
                ad != null, ad?.RuntimeSerialNumber ?? 0u);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ORBIT-DIAG] post-boot ActiveDoc probe threw");
        }

        // The RDK (Renderer Development Kit) plug-in owns
        // `RenderMaterial.FindRenderTexture`, `RenderTexture.SimulatedTexture`,
        // and the entire `Rhino.Render.*` material/texture API. In a headless
        // Rhino.Inside host RDK is NOT auto-loaded — meaning every texture
        // extraction strategy in the connector silently returns null, which
        // is the primary suspect for PRISM uploads landing with zero blobs.
        // Force-load it here and log the result so we can confirm in v0.1.14
        // diagnostics whether the RDK was the blocker.
        EnsureRdkLoaded();

        // FileImport plug-ins (OBJ / FBX / STL / IGES / STEP / DWG / DXF /
        // PLY / 3MF / SketchUp / etc.) ship as native C++ .rhp's that
        // Rhino.Inside does NOT auto-load on boot. Without an explicit
        // LoadPlugIn call, `RhinoApp.RunScript("-_Import …")` has no
        // registered handler for any non-3dm extension and silently returns
        // false, which is what was crashing every OBJ/FBX/STL/etc. job in
        // PRISM with `[OBJ-IMPORT] RhinoApp.RunScript returned false`.
        EnsureFileImportersLoaded();
    }

    /// <summary>
    /// Canonical RDK plug-in GUID — the same value Rhino uses to identify
    /// the RhinoRdk.rhp shipped in every Rhino 8 install.
    /// </summary>
    static readonly Guid RdkPlugInId = new("16592D58-4A2F-401D-BF5E-3B87741C1B1B");

    /// <summary>
    /// Snapshot of the RDK probe results captured during <see cref="EnsureRdkLoaded"/>.
    /// <see cref="Pipeline.ConvertJob"/> reads this at the start of each job
    /// so the line surfaces in the admin UI alongside the conversion logs —
    /// the host startup log itself only lives in the agent's local Serilog
    /// file, which is hard to read from PRISM admin.
    /// </summary>
    public static string? LastRdkReport { get; private set; }

    void EnsureRdkLoaded()
    {
        // Step 1: log the on-disk path Rhino has registered for the RDK GUID
        // (proves the plug-in is at least installed and discoverable). A null
        // / empty path means "not registered in this Rhino installation",
        // which is itself useful diagnostic information.
        string? rdkPath = TryGet(() => global::Rhino.PlugIns.PlugIn.PathFromId(RdkPlugInId));
        _log.LogWarning(
            "RhinoHost: RDK PathFromId({Id}) → '{Path}'",
            RdkPlugInId, rdkPath ?? "<unknown>");

        // Step 2: force-load. Returns false when the plug-in is missing OR
        // when it is already loaded (depending on Rhino version) — log it
        // either way so we can correlate against the probe result below.
        string loadResult;
        try
        {
            var ok = global::Rhino.PlugIns.PlugIn.LoadPlugIn(RdkPlugInId);
            loadResult = ok.ToString();
        }
        catch (Exception err)
        {
            loadResult = $"<threw {err.GetType().Name}: {err.Message}>";
        }
        _log.LogWarning("RhinoHost: PlugIn.LoadPlugIn(RDK) → {Result}", loadResult);

        // Step 3: exercise an RDK code path. We can't reliably call into
        // the document material collection here (ActiveDoc may legitimately
        // be null in the headless host before the first job opens a file),
        // but we CAN ask the RDK type system how many render-content types
        // it has registered. >0 means RDK is alive; 0 / throw means RDK has
        // not initialised and texture extraction will silently return null
        // throughout the connector pipeline.
        int registeredTypeCount = -1;
        string? probeError = null;
        try
        {
            var registeredTypes = global::Rhino.Render.RenderContentType.GetAllAvailableTypes();
            registeredTypeCount = registeredTypes?.Length ?? 0;
            _log.LogWarning(
                "RhinoHost: RDK probe — RenderContentType.GetAllAvailableTypes() returned {Total} types. " +
                ">0 means RDK is live and SimulatedTexture / FindRenderTexture will work.",
                registeredTypeCount);
        }
        catch (Exception probeErr)
        {
            probeError = $"{probeErr.GetType().Name}: {probeErr.Message}";
            _log.LogWarning(probeErr,
                "RhinoHost: RDK probe (RenderContentType.GetAllAvailableTypes) threw — " +
                "RDK is NOT functional in this host. Texture extraction will fail.");
        }

        // Stash a single-line summary so each job can re-emit it through the
        // WS log channel. Format keeps it grep-able and short.
        LastRdkReport =
            $"RDK PathFromId='{rdkPath ?? "<unknown>"}' LoadPlugIn={loadResult} " +
            (probeError is null
                ? $"RenderContentTypes={registeredTypeCount}"
                : $"probeThrew={probeError}");
    }

    /// <summary>
    /// Snapshot of the FileImport-plug-in warmup results captured during
    /// <see cref="EnsureFileImportersLoaded"/>. Re-emitted by the per-job
    /// probe in <see cref="RhinoFileOpener.ImportIntoFreshDoc"/> so the
    /// outcome of the warmup is visible in <c>job_logs</c>.
    /// </summary>
    public static string? LastFileImporterReport { get; private set; }

    /// <summary>
    /// Force-load every installed FileImport plug-in so
    /// <c>-_Import &lt;path&gt;</c> succeeds for non-3dm formats.
    ///
    /// <para>
    /// Filter: any plug-in whose registered display name contains "import"
    /// (case-insensitive) and does NOT contain "export". This catches every
    /// shape of name Rhino 8 ships with — <c>Import_OBJ</c>,
    /// <c>Import_FBX</c>, <c>STL Import</c>, <c>PLY - Polygon File Format
    /// Import</c>, <c>AutoCAD file import: import_ACAD</c>,
    /// <c>SketchUp Import</c>, <c>STEP Import</c>, <c>IGES Import Plug-in</c>,
    /// etc. — without us having to maintain a brittle prefix allow-list.
    /// Exporter plug-ins (<c>Export_FBX</c>, <c>3D Studio Export</c>, etc.)
    /// are skipped because loading them buys us nothing for the import path
    /// and a few of them require licences (Solidworks export, V-Ray
    /// exporter, …).
    /// </para>
    ///
    /// <para>
    /// Each <c>LoadPlugIn(Guid)</c> call is wrapped in try/catch so a single
    /// licence-blocked plug-in (Solidworks, V-Ray, etc.) cannot abort the
    /// whole warmup. Every result is logged with the <c>[OBJ-IMPORT]</c>
    /// prefix so the per-job WS log channel can correlate the warmup
    /// outcome against the per-process plug-in inventory probe in
    /// <see cref="RhinoFileOpener.LogInstalledPlugIns"/>.
    /// </para>
    ///
    /// <para>
    /// TODO (deeper fallback): if a future Rhino release changes the
    /// command-parser behaviour and <c>RhinoApp.RunScript("-_Import …")</c>
    /// still returns false even after the matching importer is loaded, the
    /// next step is to drive <see cref="global::Rhino.PlugIns.FileImportPlugIn.ReadFile"/>
    /// directly — locate the importer instance via
    /// <see cref="global::Rhino.PlugIns.PlugIn.Find(Guid)"/> (returns null
    /// for native C++ importers, so this needs reflection over
    /// <c>FileImportPlugIn.GetExtensions</c>) and call its
    /// <c>ReadFile(path, doc.RuntimeSerialNumber, FileReadOptions, ...)</c>
    /// overload. Avoid until we have evidence RunScript is the wrong API.
    /// </para>
    /// </summary>
    void EnsureFileImportersLoaded()
    {
        int matched = 0;
        int loaded = 0;
        int failed = 0;
        var failures = new List<string>(4);
        Dictionary<Guid, string>? installed = null;

        try
        {
            installed = global::Rhino.PlugIns.PlugIn.GetInstalledPlugIns();
        }
        catch (Exception err)
        {
            _log.LogWarning(err,
                "[OBJ-IMPORT] EnsureFileImportersLoaded: GetInstalledPlugIns threw — " +
                "no importer plug-ins will be force-loaded; non-3dm imports will likely fail");
            LastFileImporterReport =
                $"GetInstalledPlugIns threw {err.GetType().Name}: {err.Message}";
            return;
        }

        _log.LogWarning(
            "[OBJ-IMPORT] EnsureFileImportersLoaded: scanning {Total} installed plug-ins for FileImport candidates",
            installed.Count);

        foreach (var kvp in installed)
        {
            var name = kvp.Value ?? string.Empty;
            if (!IsFileImporterCandidate(name)) continue;

            matched++;
            string outcome;
            try
            {
                var ok = global::Rhino.PlugIns.PlugIn.LoadPlugIn(kvp.Key);
                if (ok)
                {
                    loaded++;
                    outcome = "True";
                }
                else
                {
                    failed++;
                    outcome = "False";
                    failures.Add(name);
                }
            }
            catch (Exception err)
            {
                failed++;
                outcome = $"<threw {err.GetType().Name}: {err.Message}>";
                failures.Add($"{name} ({err.GetType().Name})");
            }
            _log.LogWarning(
                "[OBJ-IMPORT] LoadPlugIn name='{Name}' id={Id} → {Outcome}",
                name, kvp.Key, outcome);
        }

        var summary =
            $"FileImporters: matched={matched} loaded={loaded} failed={failed}" +
            (failures.Count > 0 ? $" failures=[{string.Join("; ", failures)}]" : string.Empty);
        _log.LogWarning("[OBJ-IMPORT] EnsureFileImportersLoaded summary: {Summary}", summary);
        LastFileImporterReport = summary;
    }

    /// <summary>
    /// Match the registered display name of a Rhino plug-in against the
    /// "is a FileImport plug-in" heuristic used by
    /// <see cref="EnsureFileImportersLoaded"/>. Conservative on purpose: a
    /// plug-in whose name doesn't mention "import" is left alone.
    /// </summary>
    static bool IsFileImporterCandidate(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.IndexOf("import", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (name.IndexOf("export", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    public string RhinoVersion =>
        TryGet(() => global::Rhino.RhinoApp.Version.ToString()) ?? "unknown";

    /// <summary>
    /// Create a fresh RhinoDoc for a job to populate.
    /// <para>
    /// v0.1.20: uses <c>RhinoDoc.Create(null)</c> instead of
    /// <c>RhinoDoc.CreateHeadless(null)</c>. The non-headless constructor
    /// participates in the host's interactive context — RDK is hydrated
    /// against the doc, the render-mesh cache and <c>doc.Bitmaps</c> table
    /// are real, and <c>mat.RenderMaterial.FirstChild</c> resolves
    /// correctly. Headless docs were causing the connector's RDK / PBR
    /// texture-extraction strategies to return null on every material in
    /// the v0.1.14–v0.1.19 test runs.
    /// </para>
    /// <para>
    /// The new doc becomes <see cref="global::Rhino.RhinoDoc.ActiveDoc"/>;
    /// the existing single-Rhino-engine serialisation in
    /// <see cref="Pipeline.WorkerSlotPool"/> keeps concurrent jobs from
    /// stomping each other.
    /// </para>
    /// </summary>
    public global::Rhino.RhinoDoc CreateDoc() =>
        global::Rhino.RhinoDoc.Create(null)
        ?? throw new InvalidOperationException("RhinoDoc.Create returned null — is Rhino 8 installed?");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.Dispose();
    }

    static void PrependToPath(string dir)
    {
        const string name = "PATH";
        var current = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process) ?? string.Empty;
        var already = current.Split(';').Any(d =>
            d.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (!already)
            Environment.SetEnvironmentVariable(name, dir + ";" + current, EnvironmentVariableTarget.Process);
    }

    static T? TryGet<T>(Func<T> f) where T : class
    {
        try { return f(); } catch { return null; }
    }
}
