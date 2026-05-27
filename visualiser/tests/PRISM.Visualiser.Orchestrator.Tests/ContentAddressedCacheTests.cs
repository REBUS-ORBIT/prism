using System.Text;

using Xunit;

using PRISM.Visualiser.Orchestrator.OrbitApi;
using PRISM.Visualiser.Orchestrator.Tests.TestHelpers;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Direct unit tests for <see cref="ContentAddressedCache"/>: the hash
/// helper, atomic write semantics, and the sharded layout.
/// </summary>
public class ContentAddressedCacheTests
{
    [Fact]
    public void ComputeHash_ReturnsLowercaseHex64()
    {
        var hash = ContentAddressedCache.ComputeHashOfText("hello world");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        // SHA256("hello world") — well-known.
        Assert.Equal(
            "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9",
            hash);
    }

    [Fact]
    public async Task ObjectCache_RoundTrips()
    {
        using var env = new TestEnv();
        var hash = ContentAddressedCache.ComputeHashOfText("{\"id\":\"abc\"}");
        var path = await env.ContentCache.WriteObjectAsync(hash, "{\"id\":\"abc\"}", default);

        Assert.True(File.Exists(path));
        Assert.True(env.ContentCache.HasObject(hash));
        var read = await env.ContentCache.TryReadObjectAsync(hash, default);
        Assert.Equal("{\"id\":\"abc\"}", read);
    }

    [Fact]
    public async Task BlobCache_RoundTrips_AndIsSharded()
    {
        using var env = new TestEnv();
        var bytes = Encoding.UTF8.GetBytes("blob payload");
        var hash = ContentAddressedCache.ComputeHash(bytes);
        var path = await env.ContentCache.WriteBlobAsync(hash, bytes, default);

        Assert.True(File.Exists(path));
        Assert.Contains(Path.Combine("blobs", hash[..2]), path, StringComparison.Ordinal);
        var read = await env.ContentCache.TryReadBlobAsync(hash, default);
        Assert.Equal(bytes, read);
    }

    [Fact]
    public async Task TryRead_ReturnsNullOnMiss()
    {
        using var env = new TestEnv();
        Assert.Null(await env.ContentCache.TryReadObjectAsync(new string('a', 64), default));
        Assert.Null(await env.ContentCache.TryReadBlobAsync(new string('b', 64), default));
    }
}
