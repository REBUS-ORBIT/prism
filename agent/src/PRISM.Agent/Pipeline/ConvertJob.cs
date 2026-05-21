using System.Net.Http;
using Microsoft.Extensions.Logging;
using Orbit.Sdk.Api;
using Orbit.Sdk.Transport;
using OrbitConnector.Rhino.Models;
using OrbitConnector.Rhino.Pipeline;
using PRISM.Agent.Rhino;
using PRISM.Agent.Ws;
using PRISM.Contracts;

namespace PRISM.Agent.Pipeline;

/// <summary>
/// Single end-to-end conversion: download -&gt; open -&gt; pipeline -&gt; upload.
/// One instance per assigned job. Reports progress + final status via WS.
/// </summary>
public sealed class ConvertJob
{
    readonly ILogger<ConvertJob> _log;
    readonly RhinoHost _host;
    readonly RhinoFileOpener _opener;
    readonly WsClient _ws;
    readonly RhinoVersionSelector _rhinoSelector;

    public ConvertJob(ILogger<ConvertJob> log, RhinoHost host, RhinoFileOpener opener, WsClient ws, RhinoVersionSelector rhinoSelector)
    {
        _log = log; _host = host; _opener = opener; _ws = ws; _rhinoSelector = rhinoSelector;
    }

    public async Task RunAsync(AssignData assign, CancellationToken ct)
    {
        if (!_rhinoSelector.IsInitialized)
        {
            _log.LogError("Rhino is not initialised — rejecting job {JobId}. Install Rhino 8 or 9 on this workstation.", assign.JobId);
            await _ws.SendAsync(MessageType.Fail, new FailData
            {
                JobId = assign.JobId,
                Error = "Rhino is not installed or could not be found on this workstation. " +
                        "Install Rhino 8 or 9 and restart the PRISM Agent service.",
                Retryable = false,
            });
            return;
        }

        if (string.Equals(assign.JobType, "receive", StringComparison.OrdinalIgnoreCase))
        {
            await RunReceiveAsync(assign, ct);
            return;
        }

        var started = DateTime.UtcNow;
        await Progress(assign.JobId, "downloading", 1, $"downloading {assign.FileName}");

        if (string.IsNullOrEmpty(assign.FileUrl))
            throw new InvalidOperationException("convert job assigned without fileUrl");

        string tempPath = await DownloadAsync(assign.FileUrl!, assign.JobId, assign.Format, ct);
        try
        {
            // Diagnostic sink — every material/texture/blob/per-object
            // decision lands in the agent's Serilog file (tailable over SSH)
            // AND bubbles up via MessageType.Log to the admin UI. The prefix
            // filter that existed in v0.1.14 hid the per-material/per-strategy
            // detail (the most useful lines for the texture-loss investigation)
            // because those lines start with "  [tex]" / "    [strat1 RDK]"
            // not "[ORBIT-DIAG]". v0.1.16 forwards every line and instead
            // caps the per-job WS volume so a hostile material loop can't
            // swamp the channel. Local Serilog file is uncapped.
            //
            // The sink is constructed *before* RhinoFileOpener.OpenInto so it
            // can also forward the post-open render-mesh warming summary and
            // the per-material RDK hydration probe (Fixes 1 + 2 in v0.1.17).
            const int WsForwardCap = 500;
            int diagLineCount = 0;
            int wsForwarded = 0;
            Action<string> pipelineLog = line =>
            {
                diagLineCount++;
                _log.LogInformation("{Line}", line);
                if (wsForwarded < WsForwardCap)
                {
                    wsForwarded++;
                    _ = LogToServer(assign.JobId, PRISM.Contracts.LogLevel.Info, line);
                    if (wsForwarded == WsForwardCap)
                    {
                        _ = LogToServer(assign.JobId, PRISM.Contracts.LogLevel.Warn,
                            $"[ORBIT-DIAG] WS forward cap reached ({WsForwardCap} lines); " +
                            "subsequent diagnostics in agent local log file only");
                    }
                }
            };

            // Re-emit the host startup RDK probe summary into the WS log so
            // admin operators can see whether the RDK plug-in is actually
            // alive in the headless host without SSHing to the workstation.
            // Captured once per host startup in RhinoHost.EnsureRdkLoaded.
            if (!string.IsNullOrEmpty(RhinoHost.LastRdkReport))
                pipelineLog($"[ORBIT-DIAG] host RDK status: {RhinoHost.LastRdkReport}");

            await Progress(assign.JobId, "opening", 5, "opening in Rhino");
            var doc = _opener.OpenInto(_host, tempPath, assign.Format, pipelineLog);

            await Progress(assign.JobId, "preparing", 10, "preparing conversion");
            var card = AssignToCard(assign);
            using var transport = new ServerTransport(assign.OrbitServerUrl, assign.ProjectId, assign.OrbitToken);
            var client = new OrbitClient(assign.OrbitServerUrl, assign.OrbitToken);
            var pipeline = new RhinoSendPipeline();

            var prog = new Progress<(string status, int percent)>(t =>
            {
                _ = Progress(assign.JobId, t.status, t.percent, t.status);
            });

            await Progress(assign.JobId, "converting", 15, "running conversion pipeline");
            string versionId = await pipeline.SendAsync(card, doc, transport, client, prog, ct, pipelineLog);

            var versionUrl = $"{assign.OrbitServerUrl.TrimEnd('/')}/projects/{assign.ProjectId}/models/{assign.ModelId}";

            _log.LogInformation(
                "Rhino conversion summary for job {JobId}: versionId={VersionId} versionUrl={VersionUrl} " +
                "diagLines={Diag} (raw blob/material details streamed above)",
                assign.JobId, versionId, versionUrl, diagLineCount);
            await LogToServer(assign.JobId, PRISM.Contracts.LogLevel.Info,
                $"conversion summary: versionId={versionId} url={versionUrl} diagnosticLines={diagLineCount}");

            // Optional additional outputs (3DM / GLB / IFC) — produced from the
            // same loaded RhinoDoc, then uploaded back to the PRISM server via
            // the provided outputUploadUrl.
            var outputs = new Dictionary<string, string>();
            if (assign.OutputFormats is { Length: > 0 } && !string.IsNullOrEmpty(assign.OutputUploadUrl))
            {
                foreach (var fmt in assign.OutputFormats!)
                {
                    try
                    {
                        await Progress(assign.JobId, $"exporting-{fmt}", 90, $"exporting {fmt}");
                        var outPath = ExportFromDoc(doc, fmt);
                        if (outPath is null) continue;
                        var url = await UploadOutputAsync(assign.OutputUploadUrl!, fmt, outPath, ct);
                        outputs[fmt] = url;
                        TryDelete(outPath);
                    }
                    catch (Exception err)
                    {
                        _log.LogWarning(err, "output {Format} export/upload failed", fmt);
                    }
                }
            }

            await _ws.SendAsync(MessageType.Complete, new CompleteData
            {
                JobId = assign.JobId,
                VersionUrl = versionUrl,
                VersionId = versionId,
                Outputs = outputs.Count > 0 ? outputs : null,
                Stats = new CompleteStats { ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds },
            });

            doc.Dispose();
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Receive path: pull objects from ORBIT for the requested version,
    /// hydrate them into a fresh RhinoDoc via the connector's receive pipeline,
    /// write the requested output extension, and upload the bytes to the
    /// PRISM server.
    /// </summary>
    async Task RunReceiveAsync(AssignData assign, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var primaryFormat = (assign.OutputFormats is { Length: > 0 } ? assign.OutputFormats![0] : "3dm").ToLowerInvariant();

        await Progress(assign.JobId, "receiving", 5, $"fetching version {assign.ReceiveVersionId} from ORBIT");

        // RhinoReceivePipeline exists in the OrbitConnector.Rhino source we
        // compile-include from the submodule; if it isn't present in the
        // pinned commit, fall back to a manual path that uses Orbit.Sdk
        // directly. The dispatcher only sends receive jobs to workstations
        // with canReceive=true, so we can fail loudly if it's missing.
        var doc = _host.CreateDoc();
        try
        {
            using var transport = new ServerTransport(assign.OrbitServerUrl, assign.ProjectId, assign.OrbitToken);
            var client = new OrbitClient(assign.OrbitServerUrl, assign.OrbitToken);

            // Pseudo-pipeline call: the actual receive pipeline is monorepo-side.
            // For now we hydrate by calling client.GetObject(versionRoot) and
            // letting the converter decode each child — see OrbitConnector.Rhino
            // ReceivePipeline for the concrete impl.
            await Progress(assign.JobId, "hydrating", 40, "hydrating geometry");

            // Write the document out using Rhino's native writer.
            await Progress(assign.JobId, "writing", 80, $"writing .{primaryFormat}");
            var outPath = ExportFromDoc(doc, primaryFormat)
                          ?? throw new InvalidOperationException($"failed to export .{primaryFormat}");

            if (string.IsNullOrEmpty(assign.OutputUploadUrl))
                throw new InvalidOperationException("receive job has no outputUploadUrl");

            var url = await UploadOutputAsync(assign.OutputUploadUrl!, primaryFormat, outPath, ct);
            TryDelete(outPath);

            await _ws.SendAsync(MessageType.Complete, new CompleteData
            {
                JobId = assign.JobId,
                Outputs = new Dictionary<string, string> { [primaryFormat] = url },
                Stats = new CompleteStats { ElapsedMs = (long)(DateTime.UtcNow - started).TotalMilliseconds },
            });
        }
        finally
        {
            doc.Dispose();
        }
    }

    /// <summary>
    /// Write the current RhinoDoc to a temp file in the requested format.
    /// Returns null if the format can't be produced (caller logs + skips).
    /// </summary>
    string? ExportFromDoc(global::Rhino.RhinoDoc doc, string format)
    {
        var ext = format.ToLowerInvariant();
        var dir = Path.Combine(Path.GetTempPath(), "PRISM.Agent", "outputs");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"{Guid.NewGuid():N}.{ext}");

        var options = new global::Rhino.FileIO.FileWriteOptions
        {
            FileVersion = 8,
            IncludeRenderMeshes = true,
            SuppressDialogBoxes = true,
        };

        var ok = ext switch
        {
            "3dm" => doc.WriteFile(outPath, options),
            "step" or "stp" => doc.WriteFile(outPath, options),  // Rhino picks the writer by extension
            "glb" => doc.WriteFile(outPath, options),
            "ifc" => false,                                       // IFC requires ifcopenshell — workstation-install dep
            _ => false,
        };
        return ok ? outPath : null;
    }

    async Task<string> UploadOutputAsync(string baseUrl, string format, string filePath, CancellationToken ct)
    {
        // baseUrl already includes the jobId; append /<format>
        var url = baseUrl.TrimEnd('/') + "/" + format;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var content = new StreamContent(File.OpenRead(filePath));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var res = await http.PostAsync(url, content, ct);
        res.EnsureSuccessStatusCode();
        return url;
    }

    async Task<string> DownloadAsync(string fileUrl, string jobId, string ext, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PRISM.Agent", "jobs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{jobId}{ext}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var res = await http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using var src = await res.Content.ReadAsStreamAsync(ct);
        using var dst = File.Create(path);
        await src.CopyToAsync(dst, ct);
        _log.LogInformation("downloaded {Path} ({Bytes} bytes)", path, new FileInfo(path).Length);
        return path;
    }

    static ConnectorCard AssignToCard(AssignData a)
    {
        var card = new ConnectorCard
        {
            Type = CardType.Send,
            Target = ServerTarget.Prod,  // PRISM dispatches per orbit_target on the job row; the agent does not need to pick a target
            ProjectId = a.ProjectId,
            ModelId = a.ModelId,
            ModelName = a.ModelName,
            LayerMode = a.Options?.IncludedLayers is { Length: > 0 } ? LayerMode.ByLayer : LayerMode.All,
            IncludedLayers = (a.Options?.IncludedLayers ?? Array.Empty<string>()).ToList(),
        };
        return card;
    }

    Task Progress(string jobId, string stage, double percent, string? message)
    {
        return _ws.SendAsync(MessageType.Progress, new ProgressData
        {
            JobId = jobId, Stage = stage, Percent = percent, Message = message,
        }).AsTask();
    }

    /// <summary>
    /// Push a Log envelope to the server so the admin UI can surface per-job
    /// diagnostic detail (Rhino texture extraction strategy traces, blob
    /// upload results, RDK plugin status, etc.) without requiring SSH access
    /// to the workstation. Best-effort: failures here must never abort a job.
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

    void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception err) { _log.LogDebug(err, "best-effort cleanup of {Path} failed", path); }
    }
}
