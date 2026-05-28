using Serilog;

namespace PRISM.Visualiser.Orchestrator.OrbitApi;

/// <summary>
/// Resolves binary blobs (textures, raw attachments) by ORBIT blob id.
/// Cache-first: a blob present in the on-disk
/// <see cref="ContentAddressedCache"/> never hits the network. Misses
/// fan out to <see cref="IOrbitApi.GetBlobAsync"/> with bounded
/// parallelism (<see cref="MaxConcurrentDownloads"/> workers).
///
/// ORBIT blob ids are 10-character strings assigned by the server (not
/// SHA256 hashes). The cache uses them as opaque keys; no integrity
/// hash-check is performed after download because the server ID is the
/// authoritative content address.
///
/// Used by:
///   * <see cref="Converters.FromOrbit.MaterialConverter"/> to resolve
///     <c>@blob:HASH</c> texture references.
///   * The receive pipeline's eager-blob path Phase D may add when
///     pre-fetching becomes worthwhile.
/// </summary>
public sealed class BlobDownloader
{
    /// <summary>Plan §Phase 1.4 — max parallel HTTP fetches.</summary>
    public const int MaxConcurrentDownloads = 8;

    private readonly IOrbitApi _api;
    private readonly ContentAddressedCache _cache;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate;

    public BlobDownloader(IOrbitApi api, ContentAddressedCache cache, ILogger log)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _gate = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);
    }

    /// <summary>
    /// Resolve a single blob to its on-disk cache path. Hits the
    /// network only on cache miss. Uses the ORBIT blob id as the
    /// cache key (no SHA256 integrity check — ORBIT blob ids are
    /// 10-char server-assigned strings, not content hashes).
    /// </summary>
    public async Task<string> ResolveAsync(string projectId, string blobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobId);

        if (_cache.HasBlob(blobId))
        {
            _log.Verbose("blob cache hit blobId={BlobId}", blobId);
            return _cache.BlobPath(blobId);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate so we never download the same
            // blob twice from concurrent ResolveAsync callers.
            if (_cache.HasBlob(blobId))
            {
                return _cache.BlobPath(blobId);
            }

            _log.Information("blob fetch blobId={BlobId}", blobId);
            await using var stream = await _api
                .GetBlobAsync(projectId, blobId, ct)
                .ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();

            // No SHA256 integrity check: ORBIT blob ids are server-assigned
            // 10-char strings, not content hashes. Trust the server.
            return await _cache.WriteBlobAsync(blobId, bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resolve a list of blobs in parallel. Returns a hash → path map.
    /// Duplicate hashes are de-duped before fan-out so the same blob
    /// is fetched at most once per call regardless of how many times
    /// it appears in the input.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveManyAsync(
        string projectId,
        IEnumerable<string> hashes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(hashes);

        var unique = hashes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (unique.Length == 0) return results;

        var tasks = unique.Select(async h =>
        {
            var path = await ResolveAsync(projectId, h, ct).ConfigureAwait(false);
            return (h, path);
        });
        foreach (var (h, p) in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            results[h] = p;
        }
        return results;
    }
}
