using global::Rhino;
using global::Rhino.DocObjects;
using global::Rhino.FileIO;
using global::Rhino.Geometry;
using Microsoft.Extensions.Logging;

namespace PRISM.Agent.Rhino;

/// <summary>
/// Format-aware file loader. Knows which Rhino import strategy to use for
/// each supported extension.
///
/// Default strategy is <c>RhinoDoc.Read(path)</c> for native .3dm; everything
/// else is imported into a fresh doc using <see cref="FileImport"/> wrappers
/// and the format-specific import command.
/// </summary>
public sealed class RhinoFileOpener
{
    readonly ILogger<RhinoFileOpener> _log;

    public RhinoFileOpener(ILogger<RhinoFileOpener> log) { _log = log; }

    /// <summary>
    /// Extensions PRISM.Agent will accept for both <c>convert</c> and
    /// <c>pollLayers</c> jobs. The native <c>.3dm</c> path uses
    /// <see cref="OpenAsActiveDoc"/>; everything else funnels through
    /// <see cref="ImportIntoFreshDoc"/> which drives Rhino's
    /// <c>-_Import</c> command into a fresh ActiveDoc.
    /// <para>
    /// <c>.skp</c> requires Rhino's SketchUp importer plug-in; on hosts
    /// where it isn't installed the agent surfaces a
    /// <c>[OBJ-IMPORT] RhinoApp.RunScript returned false</c> error along
    /// with the per-process plugin probe so operators can correlate.
    /// </para>
    /// </summary>
    // NOTE: Rhino 8 ships ONLY export plug-ins for `.dae` (Export_DAE.rhp)
    // and `.3ds` (export_3DS.rhp). There is NO matching FileImport plug-in
    // for these extensions — confirmed against the per-process plug-in
    // inventory probe in `LogInstalledPlugIns`. Listing them here would
    // produce a confusing `[OBJ-IMPORT] RhinoApp.RunScript returned false`
    // error at import time; better to reject the upload at the server.
    // `.zip` is a "supported source format" from PRISM's perspective even
    // though RhinoFileOpener itself never opens an archive — the agent
    // pipeline calls ZipBundleExtractor.Resolve immediately after download
    // and only ever hands a primary geometry file to OpenInto /
    // ImportIntoFreshDoc. Surfacing it in this set lets the agent advertise
    // .zip to the server in the Hello message so the dispatcher routes
    // bundle uploads to this workstation just like any other format.
    public static readonly IReadOnlyCollection<string> SupportedExtensions = new[]
    {
        ".3dm",
        ".dwg", ".dxf",
        ".fbx", ".obj", ".stl", ".ply",
        ".3mf", ".skp",
        ".step", ".stp", ".iges", ".igs",
        ".zip",
    };

    /// <summary>
    /// Open the supplied path into a fresh RhinoDoc and prepare it for the
    /// connector pipeline. The optional <paramref name="diag"/> callback
    /// receives the post-open <c>[ORBIT-DIAG]</c> lines (render-mesh warming
    /// summary + per-material RDK hydration probe) so callers can forward
    /// them to the WS log channel in addition to the agent's Serilog file.
    /// </summary>
    public RhinoDoc OpenInto(RhinoHost host, string path, string formatHint, Action<string>? diag = null)
    {
        var ext = string.IsNullOrEmpty(formatHint)
            ? Path.GetExtension(path).ToLowerInvariant()
            : formatHint.ToLowerInvariant();

        if (!SupportedExtensions.Contains(ext))
            throw new NotSupportedException($"format not supported by PRISM.Agent: {ext}");

        _log.LogInformation("opening {Path} as {Ext}", path, ext);

        RhinoDoc doc;
        if (ext == ".3dm")
        {
            // v0.1.21: open via the typed RhinoCommon API so the file
            // becomes the host's ActiveDoc with full interactive context
            // (RDK hydration, doc.Bitmaps, render-mesh cache,
            // doc.RenderMaterials). v0.1.20's RunScript("-_Open ...") was
            // silently refused by Rhino.Inside because the command parser
            // expects interactive context. RhinoDoc.Open / ReadFile bypass
            // the command stack entirely. OpenHeadless was the original
            // failure — it skips RDK hydration, leaving mat.RenderMaterial
            // null on PBR materials and breaking every texture-extraction
            // strategy in the connector pipeline (v0.1.14 → v0.1.19).
            doc = OpenAsActiveDoc(path, diag);
            _log.LogInformation(
                "opened {Path}: {ObjectCount} objects doc={DocRuntimeSerial}",
                path, doc.Objects.Count, doc.RuntimeSerialNumber);
        }
        else
        {
            // Non-3dm path: fresh doc + Rhino's `-_Import` command. Detailed
            // diagnostics + per-process plugin probe live inside
            // ImportIntoFreshDoc so the convert and pollLayers code paths
            // share exactly the same import behaviour and log shape.
            doc = ImportIntoFreshDoc(host, path, ext, diag);
            _log.LogInformation(
                "imported {Path}: {ObjectCount} objects doc={DocRuntimeSerial}",
                path, doc.Objects.Count, doc.RuntimeSerialNumber);
        }

        // Fix 1 (v0.1.17): warm render meshes after headless open.
        //
        // Interactive Rhino populates each RhinoObject's Render mesh cache as
        // soon as the viewport draws the object — RhinoCommon's
        // `GetMeshes(MeshType.Render)` therefore returns real meshes inside
        // the ORBIT plug-in. The PRISM agent runs Rhino.Inside without a
        // viewport, so that cache stays empty and the connector's
        // `RhinoBrepDisplayMeshes.Extract` falls back to a per-face
        // `TessellateBrep` (one mesh per Brep face, JaggedSeams=true). That
        // failure mode is what produced the v0.1.14 "flat plane explosion"
        // (8 source Breps → 13 jagged output meshes with broken UVs).
        //
        // Forcing CreateMeshes(Render) here is exactly what the interactive
        // viewport implicitly does on first draw; it also gives the
        // RhinoMeshConverter UV backfill path real per-face UV coordinates,
        // which is critical for textured surfaces to render at all.
        WarmRenderMeshes(doc, diag);

        // Fix 2 (v0.1.17): post-open RDK / material hydration probe.
        //
        // Records doc.Materials.Count plus per-material RenderMaterial+FirstChild
        // status so the next test run can confirm whether RenderMaterial is
        // null for headless-opened docs (leading hypothesis for why all 4
        // texture-extraction strategies fail in PRISM).
        ProbeMaterialHydration(doc, diag);

        return doc;
    }

    /// <summary>
    /// Force render-mesh generation on every meshable object in <paramref name="doc"/>.
    /// See <see cref="OpenInto"/> for the diagnosis of why this is required
    /// in headless mode.
    /// </summary>
    void WarmRenderMeshes(RhinoDoc doc, Action<string>? diag)
    {
        MeshingParameters meshParams;
        try
        {
            meshParams = doc.GetCurrentMeshingParameters() ?? MeshingParameters.QualityRenderMesh;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GetCurrentMeshingParameters failed; using MeshingParameters.QualityRenderMesh");
            meshParams = MeshingParameters.QualityRenderMesh;
        }

        int warmed = 0;
        int meshesCreated = 0;
        int skipped = 0;
        int failed = 0;
        foreach (var rhinoObj in doc.Objects)
        {
            if (rhinoObj?.Geometry is null) { skipped++; continue; }
            var g = rhinoObj.Geometry;
            // Only warm meshable types — skip ClippingPlaneSurface, Hatch,
            // AnnotationBase, lights, point clouds, etc. (the connector
            // filters those out before sending anyway).
            if (g is Brep || g is Extrusion || g is SubD || g is Surface)
            {
                try
                {
                    var n = rhinoObj.CreateMeshes(MeshType.Render, meshParams, ignoreCustomParameters: false);
                    warmed++;
                    meshesCreated += n;
                }
                catch (Exception ex)
                {
                    failed++;
                    _log.LogWarning(ex, "[ORBIT-DIAG] warmRenderMesh failed for object {ObjectId}", rhinoObj.Id);
                    diag?.Invoke($"[ORBIT-DIAG] warmRenderMesh failed for object {rhinoObj.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                skipped++;
            }
        }

        var summary =
            $"[ORBIT-DIAG] warmed render meshes on {warmed} objects (created {meshesCreated} meshes; " +
            $"skipped {skipped} non-meshable; {failed} failures)";
        _log.LogInformation("{Line}", summary);
        diag?.Invoke(summary);
    }

    /// <summary>
    /// Emit one log line per material (capped) describing whether the
    /// material exposes a usable RDK RenderMaterial and whether the RDK
    /// content graph has child nodes (textures). This is the on-the-record
    /// evidence for whether the headless host actually hydrates render
    /// content after OpenHeadless.
    /// </summary>
    void ProbeMaterialHydration(RhinoDoc doc, Action<string>? diag)
    {
        try
        {
            var header = $"[ORBIT-DIAG] doc.Materials.Count={doc.Materials.Count}";
            _log.LogInformation("{Line}", header);
            diag?.Invoke(header);

            int cap = Math.Min(doc.Materials.Count, 10);
            for (int i = 0; i < cap; i++)
            {
                try
                {
                    var mat = doc.Materials[i];
                    var rm = mat?.RenderMaterial;
                    var line =
                        $"[ORBIT-DIAG] material[{i}] name='{mat?.Name ?? "<null>"}' " +
                        $"pbr={(mat?.IsPhysicallyBased ?? false)} " +
                        $"RenderMaterial={(rm != null)} " +
                        $"FirstChild={(rm?.FirstChild != null)}";
                    _log.LogInformation("{Line}", line);
                    diag?.Invoke(line);
                }
                catch (Exception inner)
                {
                    var line = $"[ORBIT-DIAG] material[{i}] probe threw {inner.GetType().Name}: {inner.Message}";
                    _log.LogWarning(inner, "[ORBIT-DIAG] material[{I}] probe threw", i);
                    diag?.Invoke(line);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ORBIT-DIAG] ProbeMaterialHydration outer failure");
            diag?.Invoke($"[ORBIT-DIAG] ProbeMaterialHydration outer failure {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// One-shot guard so the FileImport plug-in inventory is logged exactly
    /// once per agent process — useful but verbose; we don't want it on
    /// every single OBJ/FBX import.
    /// </summary>
    static int s_importerProbeRan;

    /// <summary>
    /// Open a non-native file (OBJ / FBX / STL / IGES / STEP / DXF / DWG / 3MF / PLY / SKP)
    /// by creating a fresh ActiveDoc via <see cref="RhinoHost.CreateDoc"/>
    /// and reading the file directly through the typed
    /// <c>Rhino.FileIO.File*.Read(...)</c> static API (OBJ / FBX / STL / STEP /
    /// PLY / DWG / DXF / SketchUp / DGN / Lightwave / SVG). Formats with no
    /// typed <c>Read</c> surface (currently IGES, 3MF, AMF) fall back to
    /// the legacy <c>RhinoApp.RunScript("-_Import …")</c> path.
    ///
    /// <para>
    /// HISTORY (the trail of dead ends):
    /// <list type="bullet">
    /// <item>v0.1.21 used the doc-serial <see cref="global::Rhino.RhinoApp.RunScript(uint, string, bool)"/>
    /// overload — silently returned false in Rhino.Inside for every
    /// non-3dm format.</item>
    /// <item>v0.1.22 switched to the ActiveDoc-targeted
    /// <see cref="global::Rhino.RhinoApp.RunScript(string, bool)"/> overload AND
    /// force-loaded every <c>FileImport</c> plug-in via
    /// <see cref="global::Rhino.PlugIns.PlugIn.LoadPlugIn(Guid)"/> at host
    /// boot. <c>Import_OBJ</c> reported <c>LoadPlugIn → True</c> in the
    /// warmup summary but <c>RunScript("-_Import …")</c> STILL returned
    /// false — the headless Rhino.Inside command parser refuses to
    /// dispatch <c>-_Import</c> regardless of which importer plug-ins are
    /// loaded.</item>
    /// <item>v0.1.23 (this commit) bypasses the command parser entirely
    /// by calling the per-format static reader directly. The native
    /// importer DLL is still required to be loaded (the typed API is a
    /// thin managed wrapper around the C++ reader), but the call avoids
    /// the broken command-dispatch path.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Emits <c>[OBJ-IMPORT]</c> diagnostics covering: pre-import doc state,
    /// the FileImport plug-in inventory (one-shot per process), which API
    /// surface was invoked and its return value, and post-import
    /// <c>objects/layers/blocks/materials</c> counts. These are forwarded to
    /// <c>job_logs</c> by the caller so admins can grep <c>[OBJ-IMPORT]</c>
    /// to triage a failed non-native import without SSHing to the
    /// workstation.
    /// </para>
    /// </summary>
    public static RhinoDoc ImportIntoFreshDoc(RhinoHost host, string path, string ext, Action<string>? diag = null)
    {
        // host.CreateDoc() returns a fresh doc that becomes ActiveDoc — that's
        // what makes the non-doc-serial RunScript fallback (and the typed
        // File*.Read calls that touch ActiveDoc internally) work headlessly.
        var doc = host.CreateDoc();
        var preObjects = doc.Objects.Count;
        var preLayers = doc.Layers.Count;

        // One-shot per process: list every installed plug-in's name, GUID,
        // load state, and (best-effort) type. Operators tailing the agent
        // log can confirm "yes the SketchUp / OBJ / FBX importer is loaded"
        // versus "no plug-in matches this extension" purely from this line.
        if (Interlocked.Exchange(ref s_importerProbeRan, 1) == 0)
        {
            try
            {
                LogInstalledPlugIns(diag);
            }
            catch (Exception probeErr)
            {
                diag?.Invoke(
                    $"[OBJ-IMPORT] plugin probe threw {probeErr.GetType().Name}: {probeErr.Message}");
            }
        }

        var activeMatches = global::Rhino.RhinoDoc.ActiveDoc == doc;
        diag?.Invoke(
            $"[OBJ-IMPORT] pre-import path={path} ext={ext} " +
            $"ActiveDoc==freshDoc={activeMatches} objects={preObjects} layers={preLayers}");

        // ── Primary: typed Rhino.FileIO.File*.Read ───────────────────────
        // Returns:
        //   true  — typed API ran AND returned true (treat doc as populated)
        //   false — typed API ran AND returned false / threw (give up, do
        //           NOT fall back to RunScript; RunScript was the broken
        //           path that motivated this rewrite and falling back to it
        //           would just produce a confusing second error)
        //   null  — no typed API exists for this extension; try RunScript
        bool? typed = TryReadViaTypedApi(doc, path, ext, diag);

        // ── Fallback: RunScript("-_Import …") ─────────────────────────────
        // Only run when no typed API is registered for this extension. The
        // typed-API-returned-false case is intentionally treated as a hard
        // failure: if FileObj.Read returned false in headless Rhino.Inside,
        // RunScript will too, and we want the error message to point at the
        // real underlying issue (most often a malformed input file) instead
        // of muddying the trail with two failed strategies.
        bool ok;
        if (typed == true)
        {
            ok = true;
        }
        else if (typed == false)
        {
            try { doc.Dispose(); } catch { /* best-effort */ }
            throw new IOException(
                $"[OBJ-IMPORT] typed Rhino.FileIO.File*.Read returned false for ext={ext} path={path} — " +
                $"the matching native importer is loaded but refused this file " +
                $"(see the immediately-preceding [OBJ-IMPORT] log line for which typed API was invoked; " +
                $"common causes: malformed file, unsupported sub-format, missing licence on a third-party importer)");
        }
        else
        {
            // typed == null → no per-format static Read available
            // (IGES / 3MF / AMF / etc.). Try the legacy RunScript path on
            // the off-chance the command parser handles this particular
            // extension despite the Import_OBJ regression.
            var safePath = path.Replace("\"", "\"\"");
            var script = $"-_Import \"{safePath}\" _Enter _Enter _Enter";
            diag?.Invoke($"[OBJ-IMPORT] no typed File*.Read for ext={ext}; falling back to RhinoApp.RunScript(\"-_Import …\")");
            try
            {
                ok = global::Rhino.RhinoApp.RunScript(script, false);
            }
            catch (Exception err)
            {
                try { doc.Dispose(); } catch { /* best-effort */ }
                throw new IOException(
                    $"[OBJ-IMPORT] RhinoApp.RunScript threw for ext={ext} path={path}: " +
                    $"{err.GetType().Name}: {err.Message}",
                    err);
            }

            if (!ok)
            {
                try { doc.Dispose(); } catch { /* best-effort */ }
                throw new IOException(
                    $"[OBJ-IMPORT] RhinoApp.RunScript returned false for `_Import` ext={ext} path={path} — " +
                    $"no typed Rhino.FileIO.File*.Read exists for this extension and the command-parser " +
                    $"fallback is broken in headless Rhino.Inside (see the one-shot [OBJ-IMPORT] plugin " +
                    $"list emitted earlier in this agent process)");
            }
        }

        var postObjects = doc.Objects.Count;
        var postLayers = doc.Layers.Count;
        var postBlocks = TryCount(() => doc.InstanceDefinitions.Count);
        var postMaterials = TryCount(() => doc.Materials.Count);
        diag?.Invoke(
            $"[OBJ-IMPORT] post-import ok=True ext={ext} " +
            $"objects={postObjects} layers={postLayers} blocks={postBlocks} materials={postMaterials}");

        // Probe the layer tree so admins can verify from job_logs that the
        // group→layer mapping toggle (and any future format-specific
        // options seeded above) actually produced a multi-layer doc.
        LogLayerTree(doc, diag);

        // Sanity check: a successful Read that left the doc empty is
        // almost always an "importer said yes but nothing was actually
        // imported" failure mode (e.g. STEP importer with no hierarchy
        // match, or a malformed OBJ that produced 0 valid faces). Surface
        // it as a specific error rather than letting the layer-tree walker
        // happily report "0 layers" downstream.
        if (postObjects == 0 && postLayers <= preLayers)
        {
            try { doc.Dispose(); } catch { /* best-effort */ }
            throw new IOException(
                $"[OBJ-IMPORT] ActiveDoc remained empty after import for ext={ext} path={path} " +
                $"(post: objects={postObjects} layers={postLayers}; pre: objects={preObjects} layers={preLayers}) — " +
                $"the importer returned without raising an error but produced no geometry " +
                $"(malformed file, unsupported sub-format, or silent options-dialog rejection)");
        }

        return doc;
    }

    /// <summary>
    /// Attempt to read <paramref name="path"/> into <paramref name="doc"/>
    /// using the typed per-format static reader in
    /// <c>Rhino.FileIO</c>. Returns:
    /// <list type="bullet">
    /// <item><c>true</c>  — a matching typed API existed and the read
    /// succeeded.</item>
    /// <item><c>false</c> — a matching typed API existed but returned
    /// false (or threw). The caller should NOT fall back to
    /// <c>RhinoApp.RunScript</c>; surface the failure to the user.</item>
    /// <item><c>null</c>  — no typed API is registered for this
    /// extension. The caller should fall back to the legacy RunScript
    /// path (and accept that it will probably also fail in headless
    /// Rhino.Inside).</item>
    /// </list>
    /// <para>
    /// Mapping (verified against RhinoCommon 8.31 via reflection — see
    /// <c>Rhino.FileIO.File{Obj,Fbx,Stl,Stp,Ply,Dwg,Skp,Dgn,Lwo,Svg}.Read</c>):
    /// </para>
    /// <code>
    ///   .obj         → FileObj.Read(path, doc, new FileObjReadOptions(FileReadOptions { ImportMode=true, BatchMode=true }))
    ///   .fbx         → FileFbx.Read(path, doc, new FileFbxReadOptions())
    ///   .stl         → FileStl.Read(path, doc, new FileStlReadOptions())
    ///   .stp .step   → FileStp.Read(path, doc, new FileStpReadOptions())
    ///   .ply         → FilePly.Read(path, doc, new FilePlyReadOptions())
    ///   .dwg .dxf    → FileDwg.Read(path, doc, new FileDwgReadOptions())
    ///   .skp         → FileSkp.Read(path, doc, new FileSkpReadOptions())
    ///   (.dgn, .lwo, .svg are also mapped for completeness even though
    ///    they are not in SupportedExtensions.)
    /// </code>
    /// <para>
    /// Per-format options classes are constructed with defaults; the
    /// docstring for each property suggests the Rhino-default behaviour
    /// matches the interactive importer's "OK with defaults" path, which
    /// is exactly what the legacy <c>_Enter _Enter _Enter</c> RunScript
    /// chain accepted. <see cref="FileObjReadOptions"/> uniquely accepts a
    /// <see cref="FileReadOptions"/> in its constructor; we pass one with
    /// <c>ImportMode=true</c> and <c>BatchMode=true</c> so OBJ behaves
    /// identically to the IronPython watcher path in
    /// <c>3DConvert/app/converters/rhino_conv.py</c> which runs the same
    /// command interactively inside a full Rhino install.
    /// </para>
    /// </summary>
    static bool? TryReadViaTypedApi(RhinoDoc doc, string path, string ext, Action<string>? diag)
    {
        // FileReadOptions seed for any per-format options class that
        // accepts one (currently FileObjReadOptions only). BatchMode
        // suppresses any options dialog that an interactive Rhino would
        // pop up; ImportMode tells the reader to merge into the existing
        // doc instead of replacing it.
        FileReadOptions BuildSeed() => new FileReadOptions
        {
            ImportMode = true,
            BatchMode = true,
            NewMode = false,
            OpenMode = false,
        };

        bool Invoke(string api, Func<bool> reader)
        {
            diag?.Invoke($"[OBJ-IMPORT] invoking {api} (BatchMode=True, ImportMode=True)");
            try
            {
                var result = reader();
                diag?.Invoke($"[OBJ-IMPORT] {api} returned {result}");
                return result;
            }
            catch (Exception err)
            {
                diag?.Invoke(
                    $"[OBJ-IMPORT] {api} threw {err.GetType().Name}: {err.Message}");
                return false;
            }
        }

        switch (ext)
        {
            case ".obj":
            {
                // Reflected against RhinoCommon 8.x — the live properties on
                // FileObjReadOptions are:
                //   DisplayColorFromObjMaterial (bool)
                //   IgnoreTextures              (bool)
                //   MapYtoZ                     (bool)
                //   MorphTargetOnly             (bool)
                //   ReverseGroupOrder           (bool)
                //   Split32BitTextures          (bool)
                //   UseObjGroupsAs              (enum UseObjGsAs:
                //       IgnoreObjGroups | ObjGroupsAsLayers |
                //       ObjGroupsAsGroups | ObjGroupsAsObjects)
                //   UseObjObjectsAs             (enum UseObjOsAs:
                //       IgnoreObjObjects | ObjObjectsAsLayers |
                //       ObjObjectsAsGroups | ObjObjectsAsObjects)
                //
                // The two enums above are the Rhino 8 interactive Import OBJ
                // dialog's "Group / Object" → "...As" radios. The default
                // interactive choice is "Layers" for both, which is what
                // produces one Rhino layer per `g <name>` (and `o <name>`)
                // directive in the OBJ file. Without this, every imported
                // OBJ collapses onto the active default layer and the
                // PRISM layer-selection UI is useless.
                var opts = new FileObjReadOptions(BuildSeed())
                {
                    UseObjGroupsAs = FileObjReadOptions.UseObjGsAs.ObjGroupsAsLayers,
                    UseObjObjectsAs = FileObjReadOptions.UseObjOsAs.ObjObjectsAsLayers,
                    // Leave coordinate frame untouched (Python pipeline did
                    // its own swap); leave textures enabled so .mtl
                    // resolution remains possible in a follow-up task; keep
                    // group order and 32-bit-texture splitting at defaults.
                    IgnoreTextures = false,
                    MapYtoZ = false,
                    DisplayColorFromObjMaterial = true,
                    ReverseGroupOrder = false,
                    MorphTargetOnly = false,
                    Split32BitTextures = false,
                };

                LogObjOptions(opts, diag);
                return Invoke("Rhino.FileIO.FileObj.Read", () => FileObj.Read(path, doc, opts));
            }
            case ".fbx":
            {
                var opts = new FileFbxReadOptions();
                return Invoke("Rhino.FileIO.FileFbx.Read", () => FileFbx.Read(path, doc, opts));
            }
            case ".stl":
            {
                var opts = new FileStlReadOptions();
                return Invoke("Rhino.FileIO.FileStl.Read", () => FileStl.Read(path, doc, opts));
            }
            case ".stp":
            case ".step":
            {
                var opts = new FileStpReadOptions();
                return Invoke("Rhino.FileIO.FileStp.Read", () => FileStp.Read(path, doc, opts));
            }
            case ".ply":
            {
                var opts = new FilePlyReadOptions();
                return Invoke("Rhino.FileIO.FilePly.Read", () => FilePly.Read(path, doc, opts));
            }
            case ".dwg":
            case ".dxf":
            {
                var opts = new FileDwgReadOptions();
                return Invoke("Rhino.FileIO.FileDwg.Read", () => FileDwg.Read(path, doc, opts));
            }
            case ".skp":
            {
                var opts = new FileSkpReadOptions();
                return Invoke("Rhino.FileIO.FileSkp.Read", () => FileSkp.Read(path, doc, opts));
            }
            default:
            {
                // .iges / .igs / .3mf and anything else not enumerated
                // above. RhinoCommon 8.31 exposes no static Read method
                // for these — the only callable API is the broken
                // RunScript path, which the caller will try as a final
                // fallback.
                diag?.Invoke(
                    $"[OBJ-IMPORT] no typed Rhino.FileIO.File*.Read mapped for ext={ext} " +
                    $"(IGES/3MF/AMF have only Write methods in RhinoCommon 8.31)");
                return null;
            }
        }
    }

    static int TryCount(Func<int> fn) { try { return fn(); } catch { return -1; } }

    /// <summary>
    /// Reflect over every public read/write property on
    /// <see cref="FileObjReadOptions"/> and emit one
    /// <c>[OBJ-IMPORT] FileObjReadOptions.&lt;Prop&gt;=&lt;Value&gt;</c> line
    /// per property. Lets operators confirm from <c>job_logs</c> that the
    /// group→layer mapping is actually being seeded, without needing to
    /// rebuild the agent to add a console probe.
    /// </summary>
    static void LogObjOptions(FileObjReadOptions opts, Action<string>? diag)
    {
        if (diag is null) return;
        try
        {
            var props = typeof(FileObjReadOptions).GetProperties();
            Array.Sort(props, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            var parts = new List<string>(props.Length);
            foreach (var p in props)
            {
                if (!p.CanRead) continue;
                object? v;
                try { v = p.GetValue(opts); }
                catch (Exception ex) { v = $"<get threw {ex.GetType().Name}>"; }
                parts.Add($"{p.Name}={v}");
                diag.Invoke($"[OBJ-IMPORT] FileObjReadOptions.{p.Name}={v}");
            }
            diag.Invoke($"[OBJ-IMPORT] FileObjReadOptions seeded: {string.Join(" ", parts)}");
        }
        catch (Exception ex)
        {
            diag.Invoke($"[OBJ-IMPORT] LogObjOptions threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// After a successful import, walk <paramref name="doc"/>'s layer table
    /// and emit one <c>[OBJ-IMPORT] layer[i] '&lt;path&gt;' objects=&lt;count&gt;</c>
    /// line for the first 20 layers. Useful evidence that the group→layer
    /// mapping toggle actually produced a multi-layer tree (vs everything
    /// flattened onto the default layer).
    /// </summary>
    static void LogLayerTree(RhinoDoc doc, Action<string>? diag)
    {
        if (diag is null) return;
        try
        {
            int total = doc.Layers.Count;
            diag.Invoke($"[OBJ-IMPORT] post-import layers count={total}");
            int cap = Math.Min(total, 20);
            for (int i = 0; i < cap; i++)
            {
                Layer? layer = null;
                try { layer = doc.Layers[i]; } catch { /* defensive */ }
                if (layer is null) continue;
                int objCount = -1;
                try
                {
                    var objs = doc.Objects.FindByLayer(layer);
                    objCount = objs?.Length ?? 0;
                }
                catch { /* leave -1 sentinel */ }
                var path = string.IsNullOrEmpty(layer.FullPath) ? layer.Name : layer.FullPath;
                diag.Invoke($"[OBJ-IMPORT] layer[{i}] '{path}' objects={objCount}");
            }
        }
        catch (Exception ex)
        {
            diag.Invoke($"[OBJ-IMPORT] LogLayerTree threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Best-effort dump of every installed plug-in: name, GUID, current
    /// on-disk path, and load state. Filters out blatantly-irrelevant
    /// plug-ins by name to keep the line count manageable. Tagged with
    /// <c>[OBJ-IMPORT]</c> so it shares a grep prefix with the rest of
    /// the import diagnostics.
    /// </summary>
    static void LogInstalledPlugIns(Action<string>? diag)
    {
        var installed = global::Rhino.PlugIns.PlugIn.GetInstalledPlugIns();
        diag?.Invoke($"[OBJ-IMPORT] installed-plugins count={installed.Count}");

        foreach (var kvp in installed)
        {
            var id = kvp.Key;
            var name = kvp.Value ?? "<null>";

            string pathStr = "<unknown>";
            string loadedStr = "?";
            try
            {
                pathStr = global::Rhino.PlugIns.PlugIn.PathFromId(id) ?? "<unknown>";
            }
            catch (Exception err)
            {
                pathStr = $"<PathFromId threw {err.GetType().Name}>";
            }

            try
            {
                // PlugIn.Find returns the live PlugIn instance only if it's
                // already loaded. Null = "registered but not yet loaded".
                var instance = global::Rhino.PlugIns.PlugIn.Find(id);
                loadedStr = (instance != null).ToString();
            }
            catch (Exception err)
            {
                loadedStr = $"<Find threw {err.GetType().Name}>";
            }

            diag?.Invoke($"[OBJ-IMPORT] plugin name='{name}' id={id} loaded={loadedStr} path='{pathStr}'");
        }
    }

    /// <summary>
    /// Open a <c>.3dm</c> file using the typed RhinoCommon API so the
    /// resulting doc becomes <see cref="RhinoDoc.ActiveDoc"/> with full
    /// interactive RDK / render-mesh / bitmap hydration. Shared between
    /// the convert pipeline and the layer-poll path.
    /// <para>
    /// Primary path: <see cref="RhinoDoc.Open(string, out bool)"/>. In
    /// Rhino 8 this saves and closes the current active document and
    /// promotes the newly read file to <c>ActiveDoc</c>. Returns the new
    /// doc or null on error.
    /// </para>
    /// <para>
    /// Fallback: if <see cref="RhinoDoc.Open(string, out bool)"/> returns
    /// null (some Rhino.Inside builds refuse the implicit save of an
    /// untitled boot doc), read the file straight into the existing
    /// <see cref="RhinoDoc.ActiveDoc"/> via
    /// <see cref="RhinoDoc.ReadFile"/> with
    /// <c>FileReadOptions { OpenMode = true, BatchMode = true }</c>.
    /// Operating on the live ActiveDoc preserves the doc-level RDK that
    /// the connector pipeline depends on.
    /// </para>
    /// <para>
    /// Emits a single <c>[ORBIT-DIAG] post-open ActiveDoc=...</c> line
    /// via <paramref name="diag"/> so we can confirm whether the chosen
    /// path actually gave us the interactive context.
    /// </para>
    /// </summary>
    internal static RhinoDoc OpenAsActiveDoc(string path, Action<string>? diag = null)
    {
        string? primaryError = null;
        RhinoDoc? doc = null;

        // ── Strategy A: RhinoDoc.Open(path, out _) ────────────────────
        try
        {
            doc = RhinoDoc.Open(path, out _);
            if (doc is null)
                primaryError = "RhinoDoc.Open returned null (Rhino refused to open the file)";
        }
        catch (Exception err)
        {
            primaryError = $"{err.GetType().Name}: {err.Message}";
        }

        // ── Strategy B fallback: ReadFile onto the live ActiveDoc ─────
        if (doc is null)
        {
            var existing = RhinoDoc.ActiveDoc;
            if (existing is null)
            {
                throw new IOException(
                    $"open failed: RhinoDoc.Open failed ({primaryError}) and there is no " +
                    $"ActiveDoc to ReadFile into — was RhinoCore booted without a default template?");
            }

            try
            {
                using var opts = new FileReadOptions
                {
                    OpenMode = true,
                    BatchMode = true,
                };
                // RhinoDoc.ReadFile is static in Rhino 8 — it always reads
                // into the current ActiveDoc, which is what we want here
                // because the host already holds an interactive
                // template doc with hydrated RDK.
                var ok = RhinoDoc.ReadFile(path, opts);
                if (!ok)
                {
                    throw new IOException(
                        $"open failed: RhinoDoc.Open failed ({primaryError}) and " +
                        $"RhinoDoc.ReadFile({path}) returned false");
                }
                doc = RhinoDoc.ActiveDoc ?? existing;
                diag?.Invoke(
                    $"[ORBIT-DIAG] open path=ReadFile (primary RhinoDoc.Open failed: {primaryError})");
            }
            catch (IOException) { throw; }
            catch (Exception err)
            {
                throw new IOException(
                    $"open failed: RhinoDoc.Open failed ({primaryError}) and " +
                    $"RhinoDoc.ReadFile threw {err.GetType().Name}: {err.Message}", err);
            }
        }
        else
        {
            diag?.Invoke("[ORBIT-DIAG] open path=RhinoDoc.Open");
        }

        // Single-line confirmation that the open actually produced a live
        // interactive doc with hydrated RDK content. Grep target:
        //   "post-open ActiveDoc=True doc.RenderMaterials.Count="
        int rmc = 0, bc = 0;
        try { rmc = doc.RenderMaterials.Count; } catch { /* defensive */ }
        try { bc = doc.Bitmaps.Count; } catch { /* defensive */ }
        var line =
            $"[ORBIT-DIAG] post-open ActiveDoc={(RhinoDoc.ActiveDoc == doc)} " +
            $"doc.RenderMaterials.Count={rmc} doc.Bitmaps.Count={bc}";
        diag?.Invoke(line);
        return doc;
    }
}
