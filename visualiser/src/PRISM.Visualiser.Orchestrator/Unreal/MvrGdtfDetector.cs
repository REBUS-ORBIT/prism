using System.Runtime.Versioning;
using System.Text.Json.Nodes;

using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Unreal;

/// <summary>
/// Phase J — scans the staged scene + project-level attachments to detect
/// MVR / GDTF lighting-design content. Returns the staged paths of any
/// matching files so the per-run <c>import_mvr.py</c> can pull them into
/// the UE world via the DMX plugin after the main glTF import lands.
///
/// <para>
/// Two detection sources:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Speckle objects.</b> Any node in the <see cref="StagedScene"/>
///     tree whose <see cref="StagedNode.SpeckleType"/> matches an entry
///     in <see cref="MvrGdtfTypes"/>. Today the only converter that
///     emits these types is the Phase-C <c>FallbackConverter</c>, which
///     drops the source object's full body into
///     <see cref="StagedUnknown.RawJson"/>. The detector parses the raw
///     JSON looking for a <c>displayValue</c> / <c>filePath</c> /
///     <c>blobPath</c> field that points at an already-staged file path
///     on disk. Once a Phase-J-specific converter ships, it can emit a
///     bespoke <c>StagedMvrFile</c> / <c>StagedGdtfFile</c> subtype and
///     the detector's <c>StagedUnknown</c> branch will simply stop
///     firing — no behavioural change for downstream code.
///   </description></item>
///   <item><description>
///     <b>Project-level attachments.</b> The portal can attach raw
///     <c>.mvr</c> / <c>.gdtf</c> files to an ORBIT project via the
///     server's new <c>/api/projects/:projectId/attachments</c> surface
///     (Phase J server). The agent's receive pipeline downloads those
///     alongside the version's ORBIT blobs and stages them under
///     <c>stage/{runId}/attachments/</c>. The detector enumerates that
///     directory by file extension — content-type matching is a Phase-J
///     server concern; the orchestrator trusts the extension at this
///     point in the pipeline.
///   </description></item>
/// </list>
///
/// <para>
/// The detector itself does no IO beyond reading the on-disk attachments
/// directory; all paths returned are absolute. Callers (the
/// <c>VisualiserPipeline</c> wiring) decide what to do with the empty-set
/// case — Phase J's contract is "if <see cref="MvrGdtfPaths.HasAny"/> is
/// false, the path is identical to a plain glTF import, no special-casing".
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MvrGdtfDetector
{
    /// <summary>Speckle / ORBIT type discriminators the detector recognises.</summary>
    /// <remarks>
    /// Comparison is ordinal case-insensitive — Speckle's wire format is
    /// historically inconsistent about casing of the namespace prefix
    /// (some pipelines emit <c>Orbit.</c>, others <c>ORBIT.</c>).
    /// </remarks>
    public static readonly IReadOnlySet<string> MvrGdtfTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Orbit.Objects.Lighting.MvrScene",
            "Orbit.Objects.Lighting.GdtfFixture",
        };

    /// <summary>Sub-directory of the run stage dir where project attachments are staged.</summary>
    public const string AttachmentsSubFolder = "attachments";

    /// <summary>File extension matched against the attachments dir for MVR scene files.</summary>
    public const string MvrExtension = ".mvr";

    /// <summary>File extension matched against the attachments dir for GDTF fixture files.</summary>
    public const string GdtfExtension = ".gdtf";

    /// <summary>
    /// Run the two-source detection. Returns absolute paths to every
    /// detected MVR / GDTF file. Always returns a defensive copy — the
    /// pipeline is allowed to keep the returned collection around past
    /// the detector's lifetime.
    /// </summary>
    /// <param name="scene">
    ///   The Phase-C-staged <see cref="StagedScene"/>. May be empty
    ///   (mesh-only or wholly fallback) — the detector still returns a
    ///   valid (empty) result in that case.
    /// </param>
    /// <param name="runStageDir">
    ///   Absolute path of the per-run staging directory
    ///   (e.g. <c>%LOCALAPPDATA%\PRISM.Visualiser\cache\stage\&lt;runId&gt;\</c>).
    ///   The detector looks for <c>{runStageDir}\attachments\*.mvr</c>
    ///   and <c>{runStageDir}\attachments\*.gdtf</c>. A missing
    ///   attachments dir is silently treated as "no project files".
    /// </param>
    public MvrGdtfPaths Detect(StagedScene scene, string runStageDir)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(runStageDir);

        var mvr = new List<string>();
        var gdtf = new List<string>();

        // -- Source 1: staged scene tree.
        WalkScene(scene.Root, mvr, gdtf);

        // -- Source 2: project-level attachments staged on disk.
        var attachmentsDir = Path.Combine(runStageDir, AttachmentsSubFolder);
        if (Directory.Exists(attachmentsDir))
        {
            foreach (var file in Directory.EnumerateFiles(attachmentsDir, "*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file);
                if (string.Equals(ext, MvrExtension, StringComparison.OrdinalIgnoreCase))
                {
                    mvr.Add(file);
                }
                else if (string.Equals(ext, GdtfExtension, StringComparison.OrdinalIgnoreCase))
                {
                    gdtf.Add(file);
                }
            }
        }

        return new MvrGdtfPaths(Dedupe(mvr), Dedupe(gdtf));
    }

    private static IReadOnlyList<string> Dedupe(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            // Normalise so two different spellings of the same path
            // (e.g. forward vs back slashes) collapse to one entry.
            var normalised = Path.GetFullPath(p);
            if (seen.Add(normalised)) ordered.Add(normalised);
        }
        return ordered;
    }

    private static void WalkScene(StagedNode node, List<string> mvrSink, List<string> gdtfSink)
    {
        if (MvrGdtfTypes.Contains(node.SpeckleType))
        {
            // The current converter chain (Phase C) routes unrecognised
            // types through FallbackConverter, which preserves the
            // source body in RawJson. Pull a file path out of the raw
            // body — see TryExtractStagedPath for the field-name
            // precedence list. A future Phase-J converter that emits a
            // typed StagedMvr/Gdtf record can short-circuit this branch
            // by routing through the dedicated subtype here.
            if (node is StagedUnknown unknown)
            {
                var path = TryExtractStagedPath(unknown.RawJson);
                if (path is not null)
                {
                    var lower = unknown.SpeckleType.ToLowerInvariant();
                    if (lower.Contains("mvr", StringComparison.Ordinal))
                    {
                        mvrSink.Add(path);
                    }
                    else if (lower.Contains("gdtf", StringComparison.Ordinal))
                    {
                        gdtfSink.Add(path);
                    }
                    else
                    {
                        // Type name matched but neither token present — be
                        // conservative and treat the path as MVR (the
                        // outer scene format). The artist who validates the
                        // v1.0.0-ue5.7 template smoke can re-tune this.
                        mvrSink.Add(path);
                    }
                }
            }
        }

        if (node is StagedCollection coll)
        {
            foreach (var child in coll.Children)
            {
                WalkScene(child, mvrSink, gdtfSink);
            }
        }
    }

    /// <summary>
    /// Extract a stage-relative or absolute file path from a Speckle
    /// MVR / GDTF object body. Tolerates the three field shapes the
    /// Phase-J connector(s) might emit:
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>displayValue</c> as a JSON string pointing at the staged
    ///     file (e.g. <c>"stage/abc/attachments/lighting.mvr"</c>).
    ///     This is the form the spec calls out as "displayValue points
    ///     at the actual .mvr / .gdtf bytes".
    ///   </description></item>
    ///   <item><description>
    ///     <c>blobPath</c> / <c>filePath</c> — explicit string fields
    ///     for the same purpose. Reserved for a future connector that
    ///     wants a less surprising name than <c>displayValue</c>.
    ///   </description></item>
    /// </list>
    ///
    /// Returns <c>null</c> when no usable field is found; the detector
    /// silently drops such nodes (they'll already have been logged as
    /// fallbacks during Phase C).
    /// </summary>
    public static string? TryExtractStagedPath(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;
        try
        {
            if (JsonNode.Parse(rawJson) is not JsonObject obj) return null;
            foreach (var key in new[] { "displayValue", "blobPath", "filePath" })
            {
                if (!obj.TryGetPropertyValue(key, out var node) || node is null) continue;
                if (node is JsonValue v
                    && v.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && v.TryGetValue<string>(out var s)
                    && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // RawJson is opaque — a malformed body just means "no path";
            // the per-run log already carries the warning from Phase C.
        }
        return null;
    }

    /// <summary>
    /// Two flat lists of absolute paths discovered by the detector.
    /// </summary>
    public sealed record MvrGdtfPaths(
        IReadOnlyList<string> MvrPaths,
        IReadOnlyList<string> GdtfPaths)
    {
        /// <summary>True iff either list has at least one entry.</summary>
        public bool HasAny => MvrPaths.Count > 0 || GdtfPaths.Count > 0;

        /// <summary>Total file count across both lists.</summary>
        public int TotalCount => MvrPaths.Count + GdtfPaths.Count;

        /// <summary>Empty result — used in early-return paths.</summary>
        public static MvrGdtfPaths Empty { get; } = new(
            Array.Empty<string>(),
            Array.Empty<string>());
    }
}
