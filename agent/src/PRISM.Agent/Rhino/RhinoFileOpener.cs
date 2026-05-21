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

    public static readonly IReadOnlyCollection<string> SupportedExtensions = new[]
    {
        ".3dm", ".dwg", ".dxf", ".fbx", ".obj", ".stl", ".ply",
        ".3mf", ".dae", ".step", ".stp", ".iges", ".igs",
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
            doc = host.CreateDoc();
            var ok = ImportFileInto(doc, path, ext);
            if (!ok)
                throw new IOException($"Rhino refused to import {path} (format {ext})");
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

    static bool ImportFileInto(RhinoDoc doc, string path, string ext)
    {
        // RunScript with the canonical -_Import command. Quotes around the
        // path handle spaces; the trailing _Enter accepts default options.
        var quoted = "\"" + path + "\"";
        var script = $"-_Import {quoted} _Enter _Enter _Enter";

        try
        {
            return RhinoApp.RunScript(doc.RuntimeSerialNumber, script, false);
        }
        catch (Exception err)
        {
            global::Rhino.RhinoApp.WriteLine($"PRISM.Agent: import script threw: {err.Message}");
            return false;
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
