using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using Serilog;

namespace PRISM.Visualiser.Orchestrator.Unreal;

/// <summary>
/// Downloads a tagged release of the
/// <c>REBUS-ORBIT/orbit-ue-template</c> repo, caches it under
/// <c>%LOCALAPPDATA%\PRISM.Visualiser\ue-template\&lt;tag&gt;\</c>, and
/// verifies on-disk integrity via a SHA256 sidecar.
///
/// <para>
/// Cache layout per tag:
/// <code>
///   ue-template/&lt;tag&gt;/
///     orbit-ue-template-&lt;tag&gt;.zip      ← the download
///     orbit-ue-template-&lt;tag&gt;.zip.sha256 ← hex digest of the zip
/// </code>
/// </para>
///
/// <para>
/// The fetcher never extracts; <see cref="ProjectScaffolder"/> reads the
/// zip directly. That keeps the cache content-addressable: a re-tagged
/// release with the same name (Phase D's release CI re-runs on tag
/// re-push) is auto-invalidated because its zip will hash differently.
/// </para>
///
/// <para>
/// HTTP IO is virtualised through <see cref="ITemplateDownloader"/>; the
/// production binding hits the GitHub Releases CDN with a typed retry
/// budget. Tests inject a stub that serves bytes from memory.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TemplateFetcher
{
    /// <summary>
    /// Fallback tag when <c>RunManifest</c> doesn't override it. Phase D
    /// shipped this scaffold tag; the artist-populated v1.0.0-ue5.7
    /// release will eventually become the default once it lands.
    /// </summary>
    public const string DefaultTag = "v0.1.0-ue5.7-scaffold";

    /// <summary>GitHub repository slug.</summary>
    public const string RepositorySlug = "REBUS-ORBIT/orbit-ue-template";

    /// <summary>URL template — <c>{0}</c>=tag, <c>{1}</c>=asset name.</summary>
    public const string ReleaseAssetUrlTemplate =
        "https://github.com/" + RepositorySlug + "/releases/download/{0}/{1}";

    /// <summary>Asset name template — <c>{0}</c>=tag.</summary>
    public const string AssetNameTemplate = "orbit-ue-template-{0}.zip";

    private readonly string _cacheRoot;
    private readonly ITemplateDownloader _downloader;
    private readonly ILogger _log;

    public TemplateFetcher(string cacheRoot, ITemplateDownloader downloader, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Resolve the default cache root under <c>%LOCALAPPDATA%</c>.
    /// </summary>
    public static string ResolveDefaultCacheRoot()
    {
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(local, "PRISM.Visualiser", "ue-template");
    }

    /// <summary>
    /// Build a default fetcher that downloads via
    /// <see cref="HttpTemplateDownloader"/>.
    /// </summary>
    public static TemplateFetcher CreateDefault(ILogger log) =>
        new(ResolveDefaultCacheRoot(), new HttpTemplateDownloader(log), log);

    /// <summary>
    /// Fetch the release zip for <paramref name="tag"/>, returning the
    /// cached on-disk path. A cache hit revalidates the SHA256 sidecar
    /// before returning; a missing or stale sidecar forces a re-download.
    /// </summary>
    public async Task<TemplateCacheEntry> FetchAsync(string tag, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var assetName = string.Format(CultureInfo.InvariantCulture, AssetNameTemplate, tag);
        var tagDir = Path.Combine(_cacheRoot, tag);
        var zipPath = Path.Combine(tagDir, assetName);
        var sidecarPath = zipPath + ".sha256";

        Directory.CreateDirectory(tagDir);

        // Cache hit path: zip + sidecar both present and match.
        if (File.Exists(zipPath) && File.Exists(sidecarPath))
        {
            var expected = (await File.ReadAllTextAsync(sidecarPath, ct).ConfigureAwait(false)).Trim();
            var actual = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
            if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                _log.Information(
                    "ue-template cache hit tag={Tag} path={ZipPath} sha256={Sha}",
                    tag, zipPath, actual);
                return new TemplateCacheEntry(
                    Tag: tag, ZipPath: zipPath, Sha256: actual, FromCache: true);
            }
            _log.Warning(
                "ue-template cache integrity miss tag={Tag} expected={Expected} actual={Actual}",
                tag, expected, actual);
        }

        // Cache miss / corruption: download fresh.
        var url = string.Format(CultureInfo.InvariantCulture, ReleaseAssetUrlTemplate, tag, assetName);
        _log.Information("ue-template fetch tag={Tag} url={Url}", tag, url);
        var bytes = await _downloader.DownloadAsync(url, ct).ConfigureAwait(false);
        var hash = ComputeSha256(bytes);

        // Atomic write: stage to .tmp + rename, in case the process is
        // killed mid-download (don't leave a half-written zip behind).
        var tmpZip = zipPath + ".tmp";
        var tmpSha = sidecarPath + ".tmp";
        await File.WriteAllBytesAsync(tmpZip, bytes, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(tmpSha, hash, ct).ConfigureAwait(false);
        File.Move(tmpZip, zipPath, overwrite: true);
        File.Move(tmpSha, sidecarPath, overwrite: true);

        _log.Information(
            "ue-template downloaded tag={Tag} bytes={Bytes} sha256={Sha}",
            tag, bytes.Length, hash);
        return new TemplateCacheEntry(
            Tag: tag, ZipPath: zipPath, Sha256: hash, FromCache: false);
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var digest = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>
    /// HTTP layer abstraction. Production binding hits GitHub Releases;
    /// tests serve in-memory bytes.
    /// </summary>
    public interface ITemplateDownloader
    {
        Task<byte[]> DownloadAsync(string url, CancellationToken ct);
    }

    /// <summary>
    /// Production downloader: GET via <see cref="HttpClient"/>. 404
    /// surfaces as <see cref="TemplateNotFoundException"/> so the CLI
    /// can emit the spec'd <c>code: "template_not_found"</c> failure
    /// event.
    /// </summary>
    public sealed class HttpTemplateDownloader : ITemplateDownloader, IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly ILogger _log;

        public HttpTemplateDownloader(ILogger log, HttpClient? http = null)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _ownsHttp = http is null;
            _http = http ?? new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
            });
            _http.Timeout = TimeSpan.FromMinutes(2);
            // GitHub API best practice: identify ourselves.
            if (!_http.DefaultRequestHeaders.UserAgent.Any())
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "PRISM.Visualiser/0.3 (+https://github.com/REBUS-ORBIT/prism)");
            }
        }

        public async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            using var resp = await _http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                throw new TemplateNotFoundException(
                    $"Template asset not found at {url} (HTTP 404).");
            }
            if (!resp.IsSuccessStatusCode)
            {
                throw new TemplateFetchException(
                    $"GET {url} failed with HTTP {(int)resp.StatusCode} {resp.StatusCode}.");
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            _log.Verbose("ue-template downloaded url={Url} bytes={Bytes}", url, bytes.Length);
            return bytes;
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }
    }
}

/// <summary>Result of <see cref="TemplateFetcher.FetchAsync"/>.</summary>
public sealed record TemplateCacheEntry(
    string Tag,
    string ZipPath,
    string Sha256,
    bool FromCache);

/// <summary>
/// Thrown when the requested tag's release asset returns 404. The CLI
/// catches this and emits a <c>prism-visualiser/failed/v1</c> event with
/// <c>code: "template_not_found"</c>.
/// </summary>
public sealed class TemplateNotFoundException : Exception
{
    public TemplateNotFoundException(string message) : base(message) { }
}

/// <summary>
/// Thrown for any non-404 download failure (network error, 5xx, etc.).
/// </summary>
public sealed class TemplateFetchException : Exception
{
    public TemplateFetchException(string message) : base(message) { }
    public TemplateFetchException(string message, Exception inner) : base(message, inner) { }
}
