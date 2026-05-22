using System.Net.Http;
using global::Rhino;
using global::Rhino.DocObjects;
using Microsoft.Extensions.Logging;
using PRISM.Agent.Rhino;
using PRISM.Agent.Ws;
using PRISM.Contracts;

namespace PRISM.Agent.Pipeline;

/// <summary>
/// Phase 1 of the two-phase layer-selection convert flow.
///
/// Receives a <see cref="PollLayersData"/> from the server, downloads the
/// input file, opens (or imports) it into a headless RhinoDoc, walks the
/// document's layer table, and ships the resulting tree back as a
/// <see cref="LayersData"/> envelope. The doc is then disposed — no
/// conversion is done here. The server transitions the job to
/// <c>awaiting_selection</c>; once the caller POSTs their chosen layers
/// the job re-enters the dispatcher and is sent to a <c>canConvert</c>
/// agent (typically the same one) as a normal <c>assign</c>.
///
/// Intentionally does NOT call <see cref="RhinoFileOpener.OpenInto"/> —
/// that path warms the render-mesh cache and probes RDK material
/// hydration, which is wasted work when we only need the layer tree.
/// </summary>
public sealed class PollLayersJob
{
    readonly ILogger<PollLayersJob> _log;
    readonly RhinoHost _host;
    readonly WsClient _ws;
    readonly RhinoVersionSelector _rhinoSelector;

    public PollLayersJob(ILogger<PollLayersJob> log, RhinoHost host, WsClient ws, RhinoVersionSelector rhinoSelector)
    {
        _log = log; _host = host; _ws = ws; _rhinoSelector = rhinoSelector;
    }

    public async Task RunAsync(PollLayersData poll, CancellationToken ct)
    {
        if (!_rhinoSelector.IsInitialized)
        {
            await _ws.SendAsync(MessageType.Fail, new FailData
            {
                JobId = poll.JobId,
                Error = "Rhino is not installed on this workstation. Install Rhino 8 or 9 and restart the PRISM Agent service.",
                Retryable = false,
            });
            return;
        }

        if (string.IsNullOrEmpty(poll.FileUrl))
        {
            await _ws.SendAsync(MessageType.Fail, new FailData
            {
                JobId = poll.JobId, Error = "pollLayers received without fileUrl", Retryable = false,
            });
            return;
        }

        await Progress(poll.JobId, "downloading", 5, "downloading file for layer extraction");

        string tempPath = await DownloadAsync(poll.FileUrl, poll.JobId, poll.Format, ct);
        ZipBundleExtractor.Result? bundle = null;
        RhinoDoc? doc = null;
        try
        {
            // Diagnostic sink: every [OBJ-IMPORT] / [ORBIT-DIAG] line lands in
            // the agent's Serilog file AND bubbles up to job_logs over the WS
            // Log channel so admins can grep for `[OBJ-IMPORT]` in the admin
            // UI without SSH'ing to the workstation. Mirrors the ConvertJob
            // sink pattern (with a per-job cap to avoid swamping the WS).
            const int WsForwardCap = 200;
            int wsForwarded = 0;
            Action<string> diag = line =>
            {
                _log.LogInformation("{Line}", line);
                if (wsForwarded < WsForwardCap)
                {
                    wsForwarded++;
                    _ = LogToServer(poll.JobId, PRISM.Contracts.LogLevel.Info, line);
                    if (wsForwarded == WsForwardCap)
                    {
                        _ = LogToServer(poll.JobId, PRISM.Contracts.LogLevel.Warn,
                            $"[OBJ-IMPORT] WS forward cap reached ({WsForwardCap} lines); " +
                            "subsequent diagnostics in agent local log file only");
                    }
                }
            };

            // Re-emit the host-startup FileImport plug-in warmup summary so
            // the per-job log shows whether OBJ / FBX / STEP / etc. were
            // force-loaded at boot. Captured once per agent process in
            // RhinoHost.EnsureFileImportersLoaded.
            if (!string.IsNullOrEmpty(RhinoHost.LastFileImporterReport))
                diag($"[OBJ-IMPORT] host file-importer status: {RhinoHost.LastFileImporterReport}");

            // Bundle expansion (.zip → primary geometry file + siblings on
            // disk) runs after the diag sink is wired and before the Rhino
            // open/import call. See ConvertJob for the rationale.
            string effectivePath = tempPath;
            if (tempPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await Progress(poll.JobId, "extracting", 15, "extracting zip bundle");
                bundle = ZipBundleExtractor.Resolve(tempPath, diag);
                effectivePath = bundle.PrimaryPath;
            }

            await Progress(poll.JobId, "opening", 30, "opening in Rhino (layers only)");

            // After bundle expansion the effective format is whatever
            // primary geometry file the extractor selected; for non-zip
            // inputs we honour poll.Format if present, otherwise sniff the
            // downloaded path's extension.
            var ext = bundle is not null
                ? Path.GetExtension(effectivePath).ToLowerInvariant()
                : (string.IsNullOrEmpty(poll.Format)
                    ? Path.GetExtension(tempPath)
                    : poll.Format).ToLowerInvariant();

            // Native .3dm uses the typed RhinoCommon API (full interactive
            // context, RDK, render-mesh cache). Everything else funnels
            // through the shared RhinoFileOpener.ImportIntoFreshDoc so the
            // pollLayers path produces identical [OBJ-IMPORT] diagnostics
            // and identical error shapes to ConvertJob.
            doc = ext == ".3dm"
                ? RhinoFileOpener.OpenAsActiveDoc(effectivePath, diag)
                : RhinoFileOpener.ImportIntoFreshDoc(_host, effectivePath, ext, diag);

            await Progress(poll.JobId, "extracting-layers", 70, "walking layer table");

            var nodes = BuildLayerTree(doc);
            _log.LogInformation("pollLayers: job={JobId} extracted {Count} root layer(s) from {Doc} total",
                poll.JobId, nodes.Length, doc.Layers.Count);

            await _ws.SendAsync(MessageType.Layers, new LayersData { JobId = poll.JobId, Layers = nodes });
        }
        catch (Exception err)
        {
            _log.LogError(err, "pollLayers failed for job {JobId}", poll.JobId);
            await _ws.SendAsync(MessageType.Fail, new FailData
            {
                JobId = poll.JobId,
                Error = err.Message,
                Stack = err.StackTrace,
                Retryable = true,
            });
        }
        finally
        {
            try { doc?.Dispose(); } catch { /* best effort */ }
            TryDelete(tempPath);
            if (bundle?.ExtractedDir is { } extractedDir)
                TryDeleteDir(extractedDir);
        }
    }

    /// <summary>
    /// Walks <paramref name="doc"/>.Layers and returns the forest of root
    /// layers with each layer's children nested inline. Deleted layers are
    /// skipped. <c>visible</c> tracks <c>IsVisible &amp;&amp; !IsLocked</c>
    /// to match the convention used by the Rhino connector send pipeline
    /// (locked layers do not contribute objects to a send).
    /// </summary>
    static LayerNode[] BuildLayerTree(RhinoDoc doc)
    {
        var all = doc.Layers.Where(l => !l.IsDeleted).ToList();
        var byParent = all
            .GroupBy(l => l.ParentLayerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        LayerNode ToNode(Layer layer)
        {
            var color = layer.Color;
            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            var children = byParent.TryGetValue(layer.Id, out var kids)
                ? kids.Select(ToNode).ToArray()
                : null;
            return new LayerNode
            {
                Name     = layer.Name,
                FullPath = layer.FullPath,
                Color    = hex,
                Visible  = layer.IsVisible && !layer.IsLocked,
                Children = children?.Length > 0 ? children : null,
            };
        }

        var roots = all.Where(l => l.ParentLayerId == Guid.Empty).Select(ToNode).ToArray();
        return roots;
    }

    Task Progress(string jobId, string stage, double percent, string? message) =>
        _ws.SendAsync(MessageType.Progress, new ProgressData
        {
            JobId = jobId, Stage = stage, Percent = percent, Message = message,
        }).AsTask();

    /// <summary>
    /// Best-effort: forward a single diagnostic line to the server's
    /// <c>job_logs</c> channel via the WS Log envelope so admins can see
    /// <c>[OBJ-IMPORT]</c> traces in the admin UI alongside the local
    /// Serilog file. Failures here must never abort a poll.
    /// </summary>
    Task LogToServer(string jobId, PRISM.Contracts.LogLevel level, string message)
    {
        try
        {
            return _ws.SendAsync(MessageType.Log, new LogData
            {
                JobId = jobId,
                Level = level,
                Message = message,
            }).AsTask();
        }
        catch (Exception err)
        {
            _log.LogDebug(err, "best-effort LogToServer for job {JobId} failed", jobId);
            return Task.CompletedTask;
        }
    }

    async Task<string> DownloadAsync(string fileUrl, string jobId, string ext, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PRISM.Agent", "layer-polls");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{jobId}{ext}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var res = await http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using var src = await res.Content.ReadAsStreamAsync(ct);
        using var dst = File.Create(path);
        await src.CopyToAsync(dst, ct);
        _log.LogInformation("pollLayers: downloaded {Path} ({Bytes} bytes)", path, new FileInfo(path).Length);
        return path;
    }

    void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception err) { _log.LogDebug(err, "best-effort cleanup of {Path} failed", path); }
    }

    void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception err) { _log.LogDebug(err, "best-effort cleanup of dir {Path} failed", path); }
    }
}
