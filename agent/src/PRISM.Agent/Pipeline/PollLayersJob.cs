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
        RhinoDoc? doc = null;
        try
        {
            await Progress(poll.JobId, "opening", 30, "opening in Rhino (layers only)");

            var ext = (string.IsNullOrEmpty(poll.Format)
                ? Path.GetExtension(tempPath)
                : poll.Format).ToLowerInvariant();

            doc = ext == ".3dm"
                ? RhinoDoc.OpenHeadless(tempPath) ?? throw new IOException($"failed to open {tempPath}")
                : ImportInto(_host.CreateDoc(), tempPath);

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

    static RhinoDoc ImportInto(RhinoDoc doc, string path)
    {
        var quoted = "\"" + path + "\"";
        var script = $"-_Import {quoted} _Enter _Enter _Enter";
        var ok = RhinoApp.RunScript(doc.RuntimeSerialNumber, script, false);
        if (!ok) throw new IOException($"Rhino refused to import {path} during pollLayers");
        return doc;
    }

    Task Progress(string jobId, string stage, double percent, string? message) =>
        _ws.SendAsync(MessageType.Progress, new ProgressData
        {
            JobId = jobId, Stage = stage, Percent = percent, Message = message,
        }).AsTask();

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
}
