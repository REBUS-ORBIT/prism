using Serilog;

namespace PRISM.Visualiser.Orchestrator.OrbitApi;

/// <summary>
/// Resolves binary blobs (textures, raw attachments) by SHA256 hash.
/// Cache-first: a blob present in the on-disk
/// <see cref="ContentAddressedCache"/> never hits the network. Misses
/// fan out to <see cref="IOrbitApi.GetBlobAsync"/> with bounded
/// parallelism (<see cref="MaxConcurrentDownloads"/> workers).
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
    /// network only on cache miss. Throws <see cref="OrbitApiException"/>
    /// when the server returns a hash that doesn't match the request
    /// — that's a corruption / mis-routing condition no retry will fix.
    /// </summary>
    public async Task<string> ResolveAsync(string projectId, string hash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        if (_cache.HasBlob(hash))
        {
            _log.Verbose("blob cache hit hash={Hash}", hash);
            return _cache.BlobPath(hash);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside the gate so we never download the same
            // blob twice from concurrent ResolveAsync callers.
            if (_cache.HasBlob(hash))
            {
                return _cache.BlobPath(hash);
            }

            _log.Information("blob fetch hash={Hash}", hash);
            await using var stream = await _api
                .GetBlobAsync(projectId, hash, ct)
                .ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();

            var actual = ContentAddressedCache.ComputeHash(bytes);
            if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new OrbitApiException(
                    $"Blob integrity failure: expected hash '{hash}', server returned content hashing to '{actual}'.");
            }

            return await _cache.WriteBlobAsync(hash, bytes, ct).ConfigureAwait(false);
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
