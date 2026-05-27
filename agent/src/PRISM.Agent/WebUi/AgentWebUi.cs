using System.IO;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PRISM.Agent.Config;
using PRISM.Agent.Tray;
using PRISM.Contracts;

namespace PRISM.Agent.WebUi;

/// <summary>
/// Tiny hosted HTTP server that exposes the agent's settings + watcher
/// pause/resume controls in a browser.  Bound to <c>localhost:7421</c> by
/// default; the user can flip <see cref="AgentConfig.WebUiBindAll"/> to
/// expose it on the LAN (no auth — only do this on trusted networks).
///
/// Routes:
///   GET  /                       single-page HTML
///   GET  /api/state              full snapshot for the UI
///   POST /api/config             apply <see cref="ConfigUpdate"/>
///   POST /api/watcher/pause      pause job acceptance
///   POST /api/watcher/resume     resume
///   POST /api/agent/restart      cleanly exit and self-relaunch
///   POST /api/agent/update       check GitHub and apply new release if available
///   GET  /api/logs?n=200         tail buffered log lines
///   GET  /api/health             liveness ping
///
/// The server is intentionally small — no template engine, no WebSockets,
/// no DI container, just <see cref="HttpListener"/>.  When this surface
/// grows past "settings page" it should move to Kestrel + minimal APIs.
/// </summary>
public sealed class AgentWebUi : IHostedService, IAsyncDisposable
{
    readonly ILogger<AgentWebUi> _log;
    readonly AgentControlPlane _plane;
    readonly TrayLoggerProvider? _logBuf;
    readonly AgentConfig _cfg;

    HttpListener? _listener;
    CancellationTokenSource? _cts;
    Task? _loop;

    static readonly JsonSerializerSettings _json = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore,
    };

    // Cached at first request — the rendered page with the PRISM logo
    // baked into the header as a base64 data: URL. Building this once avoids
    // re-reading + re-encoding ~90 KB of PNG on every GET /.
    static readonly Lazy<string> _renderedIndex = new(BuildIndexHtml);

    public AgentWebUi(
        ILogger<AgentWebUi> log,
        AgentControlPlane plane,
        AgentConfig cfg,
        IServiceProvider sp)
    {
        _log = log;
        _plane = plane;
        _cfg = cfg;
        _logBuf = sp.GetService<TrayLoggerProvider>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_cfg.WebUiPort <= 0)
        {
            _log.LogInformation("web UI disabled (webUiPort=0)");
            return Task.CompletedTask;
        }

        var prefix = _cfg.WebUiBindAll
            ? $"http://+:{_cfg.WebUiPort}/"
            : $"http://localhost:{_cfg.WebUiPort}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5 /* access denied */)
        {
            _log.LogWarning(ex,
                "web UI failed to bind {Prefix} — agent process is not allowed to "
                + "register that URL ACL. Either run agent elevated or `netsh http "
                + "add urlacl url={Prefix} user=Everyone`.",
                prefix, prefix);
            _listener.Close();
            _listener = null;
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.LogInformation("web UI listening on {Prefix}", prefix);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* nop */ }
        try { _listener?.Stop(); } catch { /* nop */ }
        if (_loop is { } l) { try { await l; } catch { /* nop */ } }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _listener?.Close();
        _cts?.Dispose();
    }

    // -----------------------------------------------------------------

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }

            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
    }

    async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";

            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "/";
            if (string.IsNullOrEmpty(path)) path = "/";

            switch ((req.HttpMethod, path))
            {
                case ("GET", "/"):
                case ("GET", ""):
                    await WriteHtmlAsync(res, _renderedIndex.Value);
                    break;

                case ("GET", "/api/state"):
                    await WriteJsonAsync(res, BuildState());
                    break;

                case ("GET", "/api/health"):
                    await WriteJsonAsync(res, new { ok = true });
                    break;

                case ("GET", "/api/logs"):
                    {
                        int n = 200;
                        var qs = req.Url?.Query ?? "";
                        var match = System.Text.RegularExpressions.Regex.Match(qs, @"[?&]n=(\d+)");
                        if (match.Success) int.TryParse(match.Groups[1].Value, out n);
                        n = Math.Clamp(n, 1, 2000);
                        var snapshot = _logBuf?.GetSnapshot() ?? Array.Empty<string>();
                        var lines = snapshot.Skip(Math.Max(0, snapshot.Count - n)).ToArray();
                        await WriteJsonAsync(res, new { lines });
                        break;
                    }

                case ("POST", "/api/config"):
                    {
                        var body = await ReadBodyAsync(req);
                        var update = JsonConvert.DeserializeObject<ConfigUpdate>(body, _json) ?? new ConfigUpdate();
                        var result = await _plane.ApplyAsync(update);
                        await WriteJsonAsync(res, new
                        {
                            ok = true,
                            restartRequired = result.RestartRequired,
                            state = BuildState(),
                        });
                        break;
                    }

                case ("POST", "/api/watcher/pause"):
                    await _plane.PauseAsync();
                    await WriteJsonAsync(res, new { ok = true, state = BuildState() });
                    break;

                case ("POST", "/api/watcher/resume"):
                    _plane.Resume();
                    await WriteJsonAsync(res, new { ok = true, state = BuildState() });
                    break;

                case ("POST", "/api/agent/restart"):
                    {
                        // Read and discard body; reason field accepted but
                        // currently only logged. Reply BEFORE scheduling
                        // exit so the caller's fetch sees a 200.
                        var body = await ReadBodyAsync(req);
                        string? reason = null;
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            try
                            {
                                var probe = JsonConvert.DeserializeObject<RestartBody>(body, _json);
                                reason = probe?.Reason;
                            }
                            catch { /* tolerate junk */ }
                        }
                        await WriteJsonAsync(res, new { ok = true, restarting = true });
                        _ = _plane.RestartAsync(reason);
                        break;
                    }

                case ("POST", "/api/agent/update"):
                    {
                        var body = await ReadBodyAsync(req);
                        string? tag = null;
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            try
                            {
                                var probe = JsonConvert.DeserializeObject<UpdateBody>(body, _json);
                                tag = probe?.Tag;
                            }
                            catch { /* tolerate junk */ }
                        }
                        var outcome = await _plane.CheckAndApplyUpdateAsync(tag);
                        if (outcome.AlreadyRunning)
                        {
                            // 409 Conflict — another update is in flight on
                            // this agent. Friendlier to surface than the
                            // generic 502 below so the admin UI can show a
                            // "wait, then retry" message.
                            res.StatusCode = 409;
                            await WriteJsonAsync(res, new
                            {
                                ok            = false,
                                alreadyRunning = true,
                                error         = outcome.Error,
                            });
                        }
                        else if (outcome.Error is not null)
                        {
                            res.StatusCode = 502;
                            await WriteJsonAsync(res, new { ok = false, error = outcome.Error });
                        }
                        else if (!outcome.UpdateAvailable)
                        {
                            await WriteJsonAsync(res, new
                            {
                                ok = true,
                                downloading = false,
                                version = $"v{_plane.AgentVersion}",
                                message = "already up to date",
                            });
                        }
                        else
                        {
                            await WriteJsonAsync(res, new
                            {
                                ok = true,
                                downloading = true,
                                tag = outcome.Tag,
                                message = "downloading update in background",
                            });
                        }
                        break;
                    }

                default:
                    res.StatusCode = 404;
                    await WriteJsonAsync(res, new { error = "not_found", path });
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "web UI request failed: {Method} {Path}", req.HttpMethod, req.Url?.AbsolutePath);
            try
            {
                res.StatusCode = 500;
                await WriteJsonAsync(res, new { error = ex.Message });
            }
            catch { /* nothing more we can do */ }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { /* nop */ }
        }
    }

    object BuildState()
    {
        var cfg = _plane.Config;
        return new
        {
            agent = new
            {
                version = _plane.AgentVersion,
                connected = _plane.IsConnected,
                paused = _plane.IsPaused,
                slotsBusy = _plane.SlotsBusy,
                machineId = cfg.MachineId,
                supportedFormats = _plane.SupportedFormats,
            },
            config = new
            {
                prismUrl    = cfg.PrismUrl,
                nodeName    = cfg.NodeName,
                slots       = cfg.Slots,
                roles       = cfg.Roles.Select(r => r.ToString().ToLowerInvariant()).ToArray(),
                rhinoVersion = cfg.RhinoVersion,
                logDir      = cfg.LogDir,
                webUiPort   = cfg.WebUiPort,
                webUiBindAll = cfg.WebUiBindAll,
                // Visualiser (Phase A scaffold)
                unrealEngineRoot        = cfg.UnrealEngineRoot,
                unrealTemplateTag       = cfg.UnrealTemplateTag,
                visualiserMaxConcurrent = cfg.VisualiserMaxConcurrent,
                visualiserGpuCheck      = cfg.VisualiserGpuCheck,
            },
            availableRoles = Enum.GetNames(typeof(AgentRole)).Select(s => s.ToLowerInvariant()).ToArray(),
        };
    }

    static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        return await sr.ReadToEndAsync();
    }

    static async Task WriteJsonAsync(HttpListenerResponse res, object payload)
    {
        var json = JsonConvert.SerializeObject(payload, _json);
        var bytes = Encoding.UTF8.GetBytes(json);
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    static async Task WriteHtmlAsync(HttpListenerResponse res, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        res.ContentType = "text/html; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    sealed class RestartBody { public string? Reason { get; set; } }
    sealed class UpdateBody  { public string? Tag    { get; set; } }

    /// <summary>
    /// Reads <c>Assets/prism-logo.png</c> next to the executable, base64-
    /// encodes it, and substitutes the result into the
    /// <see cref="IndexHtml.LogoToken"/> placeholder so the page header
    /// renders the brand mark without an extra HTTP route. Falls back to
    /// an empty <c>src</c> when the asset is missing — CSS hides the
    /// resulting broken image (<c>img[src=""] { display: none; }</c>).
    /// </summary>
    static string BuildIndexHtml()
    {
        var dataUrl = string.Empty;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "prism-logo.png");
            if (File.Exists(path))
            {
                var bytes = File.ReadAllBytes(path);
                dataUrl = "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
        }
        catch { /* fall through to empty string */ }

        return IndexHtml.Template.Replace(IndexHtml.LogoToken, dataUrl);
    }
}
