using System.IO.Compression;

namespace PRISM.Agent.Rhino;

/// <summary>
/// Resolves a downloaded `.zip` bundle into a primary geometry file on disk.
///
/// Users frequently want to upload an OBJ together with its .mtl + texture
/// bitmaps (or an FBX with its sidecar textures, or any multi-file format).
/// PRISM's job pipeline only ships one file from server → agent, so the
/// caller bundles everything into a <c>.zip</c> and the agent re-expands it
/// here before handing the primary geometry file to <c>RhinoFileOpener</c>.
///
/// The agent extracts the archive into a sibling directory next to the
/// downloaded zip and selects ONE primary geometry file using the policy
/// documented in <see cref="Resolve"/>. The .mtl / texture siblings remain
/// next to the primary file on disk so Rhino's importer can resolve them
/// via the standard relative-path lookup.
///
/// Diagnostic lines are emitted via the supplied <c>diag</c> callback under
/// the <c>[ZIP-BUNDLE]</c> tag so they land in <c>job_logs</c> alongside the
/// existing <c>[OBJ-IMPORT]</c> / <c>[ORBIT-DIAG]</c> traces.
/// </summary>
public static class ZipBundleExtractor
{
    /// <summary>
    /// Hard cap on the cumulative bytes written across all entries in a single
    /// archive. Protects the workstation from zip-bomb resource exhaustion.
    /// </summary>
    public const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Hard cap on the uncompressed size of any single entry. A pathological
    /// archive containing one ~50 GiB entry would be caught here.
    /// </summary>
    public const long MaxEntryBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Extensions PRISM considers a candidate "primary geometry" file in a
    /// bundle. Mirrors <see cref="RhinoFileOpener.SupportedExtensions"/>
    /// minus <c>.zip</c> itself so we never recurse into nested archives.
    /// Priority is left-to-right when picking between basename ties: an
    /// archive carrying both <c>foo.obj</c> and <c>foo.stl</c> at the same
    /// depth/size will resolve to the <c>.obj</c>.
    /// </summary>
    static readonly string[] PrimaryExtPriority = new[]
    {
        ".obj", ".fbx", ".stl", ".3dm", ".ply",
        ".stp", ".step", ".iges", ".igs",
        ".dwg", ".dxf", ".skp", ".3mf",
    };

    /// <summary>Per-archive result: chosen geometry path + directory to clean up.</summary>
    public sealed record Result(string PrimaryPath, string? ExtractedDir, int FileCount);

    /// <summary>
    /// If <paramref name="downloadedPath"/> ends with <c>.zip</c>, extract it
    /// next to the archive and return the chosen primary geometry file plus
    /// the directory the caller should delete on job teardown. Otherwise
    /// returns the input path unchanged with a null extraction dir.
    ///
    /// Selection policy when multiple primary candidates exist:
    /// <list type="number">
    /// <item>Prefer a candidate whose basename (without extension) equals the
    /// zip's basename (case-insensitive). e.g. <c>model.zip</c> → <c>model.obj</c>.</item>
    /// <item>Otherwise prefer the candidate at the shallowest directory
    /// depth (closest to the archive root).</item>
    /// <item>Otherwise prefer the largest candidate by byte length.</item>
    /// <item>Otherwise tie-break alphabetically on the full relative path.</item>
    /// </list>
    ///
    /// Throws <see cref="IOException"/> on a malformed archive, zip-slip
    /// traversal, zip-bomb budget overflow, or when no supported geometry
    /// extension is present. The caller is expected to surface the error
    /// to the WS Fail channel; the partially-extracted directory (if any)
    /// is deleted before throwing.
    /// </summary>
    public static Result Resolve(string downloadedPath, Action<string>? diag)
    {
        if (string.IsNullOrEmpty(downloadedPath))
            throw new ArgumentException("downloadedPath must be non-empty", nameof(downloadedPath));

        if (!downloadedPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return new Result(downloadedPath, null, 0);

        var zipDir = Path.GetDirectoryName(downloadedPath)!;
        var zipBaseName = Path.GetFileNameWithoutExtension(downloadedPath);
        // Sibling dir, prefixed so we never collide with another job's tempPath:
        //   /tmp/PRISM.Agent/jobs/<jobId>.zip
        //   /tmp/PRISM.Agent/jobs/<jobId>__extracted/
        var extractedDir = Path.Combine(zipDir, $"{zipBaseName}__extracted");
        // Defensive: if a previous run left this dir behind (e.g. crash mid-extract),
        // wipe it before we start so we don't pick a stale primary by accident.
        try
        {
            if (Directory.Exists(extractedDir))
                Directory.Delete(extractedDir, true);
        }
        catch (Exception cleanupErr)
        {
            diag?.Invoke($"[ZIP-BUNDLE] could not pre-clean {extractedDir}: {cleanupErr.GetType().Name}: {cleanupErr.Message}");
        }
        Directory.CreateDirectory(extractedDir);

        var rootFull = Path.GetFullPath(extractedDir) + Path.DirectorySeparatorChar;
        int extractedCount = 0;
        long extractedBytes = 0;
        var extensionsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = ZipFile.OpenRead(downloadedPath);
            foreach (var entry in archive.Entries)
            {
                // Skip directory entries (FullName ends with '/' and Length == 0).
                if (string.IsNullOrEmpty(entry.Name) && entry.Length == 0)
                    continue;

                // Reject absolute paths and path-traversal attempts up-front so
                // we never even touch the filesystem outside extractedDir. The
                // additional Path.GetFullPath check below catches anything that
                // slips past these heuristics (e.g. symlink entries).
                var rel = entry.FullName.Replace('\\', '/');
                if (rel.StartsWith("/") || (rel.Length > 1 && rel[1] == ':') || rel.Contains("../") || rel.Contains("/..") || rel == "..")
                {
                    diag?.Invoke($"[ZIP-BUNDLE] refusing entry with unsafe path: {entry.FullName}");
                    throw new IOException($"[ZIP-BUNDLE] zip contains entry with absolute / traversal path: {entry.FullName}");
                }

                // Cap individual entry size BEFORE we start writing — Length
                // is the uncompressed size reported by the central directory.
                if (entry.Length > MaxEntryBytes)
                {
                    diag?.Invoke($"[ZIP-BUNDLE] entry exceeds {MaxEntryBytes / (1024 * 1024)} MiB cap: {entry.FullName} ({entry.Length} bytes)");
                    throw new IOException($"[ZIP-BUNDLE] zip entry too large: {entry.FullName} ({entry.Length} bytes; cap={MaxEntryBytes})");
                }

                var destPath = Path.GetFullPath(Path.Combine(extractedDir, rel));
                if (!destPath.StartsWith(rootFull, StringComparison.Ordinal))
                {
                    diag?.Invoke($"[ZIP-BUNDLE] refusing entry that escapes extraction root: {entry.FullName} -> {destPath}");
                    throw new IOException($"[ZIP-BUNDLE] zip-slip detected: {entry.FullName}");
                }

                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                // Directory-only entry — already created the dir, nothing to extract.
                if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    continue;

                using (var src = entry.Open())
                using (var dst = File.Create(destPath))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        extractedBytes += n;
                        if (extractedBytes > MaxExtractedBytes)
                        {
                            diag?.Invoke($"[ZIP-BUNDLE] cumulative extraction exceeded {MaxExtractedBytes / (1024 * 1024)} MiB cap; aborting");
                            throw new IOException($"[ZIP-BUNDLE] zip cumulative size cap exceeded ({extractedBytes} > {MaxExtractedBytes})");
                        }
                        dst.Write(buffer, 0, n);
                    }
                }

                extractedCount++;
                var ext = Path.GetExtension(rel).ToLowerInvariant();
                if (!string.IsNullOrEmpty(ext)) extensionsSeen.Add(ext);

                if (extractedCount <= 50)
                {
                    var size = new FileInfo(destPath).Length;
                    diag?.Invoke($"[ZIP-BUNDLE] file: {rel} size={size}");
                }
                else if (extractedCount == 51)
                {
                    diag?.Invoke("[ZIP-BUNDLE] (suppressing per-file lines for entries 51+)");
                }
            }
        }
        catch (IOException) { TryWipe(extractedDir); throw; }
        catch (Exception err)
        {
            TryWipe(extractedDir);
            diag?.Invoke($"[ZIP-BUNDLE] failed to extract: {err.GetType().Name}: {err.Message}");
            throw new IOException($"[ZIP-BUNDLE] failed to extract: {err.GetType().Name}: {err.Message}", err);
        }

        diag?.Invoke($"[ZIP-BUNDLE] extracted {extractedCount} files from {downloadedPath} to {extractedDir} (bytes={extractedBytes})");

        // Walk the extracted tree for primary-geometry candidates.
        var candidates = new List<(string FullPath, string RelPath, string Ext, int Depth, long Size)>();
        foreach (var ext in PrimaryExtPriority)
        {
            foreach (var match in Directory.EnumerateFiles(extractedDir, "*" + ext, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(extractedDir, match).Replace('\\', '/');
                var depth = rel.Count(c => c == '/');
                long size;
                try { size = new FileInfo(match).Length; } catch { size = 0; }
                candidates.Add((match, rel, ext, depth, size));
            }
        }

        if (candidates.Count == 0)
        {
            var extList = extensionsSeen.Count == 0 ? "<none>" : string.Join(", ", extensionsSeen.OrderBy(s => s));
            diag?.Invoke($"[ZIP-BUNDLE] no supported geometry file in zip — extensions found: {extList}");
            TryWipe(extractedDir);
            throw new IOException(
                $"[ZIP-BUNDLE] no supported geometry file in zip — extensions found: {extList}; " +
                $"supported: {string.Join(", ", PrimaryExtPriority)}");
        }

        diag?.Invoke("[ZIP-BUNDLE] candidates: [" + string.Join(", ", candidates.Select(c => $"{c.RelPath} ({c.Size}B, depth={c.Depth})")) + "]");

        // Selection.
        string reason;
        (string FullPath, string RelPath, string Ext, int Depth, long Size) chosen;

        // (1) basename match
        var basenameMatch = candidates
            .Where(c => string.Equals(Path.GetFileNameWithoutExtension(c.RelPath), zipBaseName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => Array.IndexOf(PrimaryExtPriority, c.Ext))
            .ThenBy(c => c.Depth)
            .ThenByDescending(c => c.Size)
            .ThenBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (basenameMatch != default)
        {
            chosen = basenameMatch;
            reason = "basename-match";
        }
        else
        {
            // (2) shallowest depth, (3) largest size, (4) alphabetical
            chosen = candidates
                .OrderBy(c => c.Depth)
                .ThenByDescending(c => c.Size)
                .ThenBy(c => Array.IndexOf(PrimaryExtPriority, c.Ext))
                .ThenBy(c => c.RelPath, StringComparer.OrdinalIgnoreCase)
                .First();
            // Distinguish reason for diagnostics: only one candidate is a degenerate case
            if (candidates.Count == 1) reason = "first";
            else if (candidates.Count(c => c.Depth == chosen.Depth) == 1) reason = "shallowest";
            else reason = "largest";
        }

        diag?.Invoke($"[ZIP-BUNDLE] selected: {chosen.RelPath} (reason={reason})");
        diag?.Invoke($"[ORBIT-DIAG] zip bundle expanded: original={downloadedPath} primary={chosen.FullPath} siblings={Math.Max(0, extractedCount - 1)}");

        return new Result(chosen.FullPath, extractedDir, extractedCount);
    }

    /// <summary>
    /// Best-effort recursive delete of <paramref name="dir"/>; swallows any
    /// failure so the caller can rethrow the original IOException without
    /// losing context.
    /// </summary>
    static void TryWipe(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* best effort */ }
    }
}
