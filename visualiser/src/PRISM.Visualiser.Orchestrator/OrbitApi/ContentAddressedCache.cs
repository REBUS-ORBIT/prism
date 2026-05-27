using System.Security.Cryptography;
using System.Text;

using PRISM.Visualiser.Orchestrator.Cache;

namespace PRISM.Visualiser.Orchestrator.OrbitApi;

/// <summary>
/// Disk-backed SHA256-keyed cache for ORBIT object JSON + binary blobs.
///
/// Layout (matching <see cref="CacheRoot"/>):
/// <code>
///   %LOCALAPPDATA%\PRISM.Visualiser\cache\objects\{hash[..2]}\{hash}.json
///   %LOCALAPPDATA%\PRISM.Visualiser\cache\blobs\{hash[..2]}\{hash}.bin
/// </code>
///
/// Every write goes through a temp file + atomic <see cref="File.Move(string,string,bool)"/>:
/// concurrent visualiser runs hashing the same blob never see a torn
/// half-written file. Reads are plain <see cref="File.OpenRead(string)"/>;
/// the OS file system gives us read consistency for free once the
/// rename has committed.
///
/// All public APIs are thread-safe.
/// </summary>
public sealed class ContentAddressedCache
{
    /// <summary>Hash prefix used for the directory shard. Two bytes / four hex.</summary>
    private const int ShardChars = 2;

    private readonly CacheRoot _root;

    public ContentAddressedCache(CacheRoot root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>Compute the lowercase hex SHA256 of <paramref name="data"/>.</summary>
    public static string ComputeHash(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Compute the SHA256 of <paramref name="json"/> as UTF-8 bytes.</summary>
    public static string ComputeHashOfText(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ComputeHash(bytes);
    }

    /// <summary>Resolve the cache path for the object with content hash <paramref name="hash"/>.</summary>
    public string ObjectPath(string hash)
    {
        ValidateHash(hash);
        return Path.Combine(_root.Objects, ShardOf(hash), hash + ".json");
    }

    /// <summary>Resolve the cache path for the blob with content hash <paramref name="hash"/>.</summary>
    public string BlobPath(string hash)
    {
        ValidateHash(hash);
        return Path.Combine(_root.Blobs, ShardOf(hash), hash + ".bin");
    }

    /// <summary>Returns true if the object cache holds <paramref name="hash"/>.</summary>
    public bool HasObject(string hash) => File.Exists(ObjectPath(hash));

    /// <summary>Returns true if the blob cache holds <paramref name="hash"/>.</summary>
    public bool HasBlob(string hash) => File.Exists(BlobPath(hash));

    /// <summary>
    /// Read the cached object JSON. Returns <c>null</c> on cache miss.
    /// </summary>
    public async Task<string?> TryReadObjectAsync(string hash, CancellationToken ct)
    {
        var path = ObjectPath(hash);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    /// <summary>Read the cached blob bytes. Returns <c>null</c> on cache miss.</summary>
    public async Task<byte[]?> TryReadBlobAsync(string hash, CancellationToken ct)
    {
        var path = BlobPath(hash);
        if (!File.Exists(path)) return null;
        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write an object JSON body atomically. Returns the cache path
    /// the body was written to.
    /// </summary>
    public async Task<string> WriteObjectAsync(string hash, string json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        var path = ObjectPath(hash);
        await WriteAtomicallyAsync(path, Encoding.UTF8.GetBytes(json), ct)
            .ConfigureAwait(false);
        return path;
    }

    /// <summary>Write a blob's bytes atomically. Returns the cache path.</summary>
    public async Task<string> WriteBlobAsync(string hash, byte[] bytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var path = BlobPath(hash);
        await WriteAtomicallyAsync(path, bytes, ct).ConfigureAwait(false);
        return path;
    }

    private static async Task WriteAtomicallyAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                $"Cache path '{path}' has no parent directory.");
        Directory.CreateDirectory(dir);

        // Temp file in the same directory as the target so File.Move
        // is a same-volume rename (atomic on NTFS) rather than a copy.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var fs = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true))
            {
                await fs.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            // Best-effort cleanup if the move failed.
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* leave for the next run */ }
            }
        }
    }

    private static string ShardOf(string hash) => hash[..ShardChars];

    private static void ValidateHash(string hash)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);
        if (hash.Length < ShardChars)
        {
            throw new ArgumentException(
                $"Cache hash '{hash}' is shorter than the {ShardChars}-char shard prefix.",
                nameof(hash));
        }
    }
}
