using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using PRISM.Visualiser.Orchestrator.Tests.TestHelpers;
using PRISM.Visualiser.Orchestrator.Unreal;

namespace PRISM.Visualiser.Orchestrator.Tests;

/// <summary>
/// Smoke Test 8 — <see cref="TemplateFetcher"/> cache hit / miss + SHA256
/// integrity flow. Mocks the HTTP layer with a counting downloader; a
/// real GitHub Releases fetch is exercised manually as part of the
/// per-PR verification checklist (see PR description).
/// </summary>
[SupportedOSPlatform("windows")]
public class TemplateFetcherTests
{
    [Fact]
    public async Task FirstFetchHitsHttp_SecondFetchHitsCache()
    {
        using var env = new TestEnv();
        var bytes = Encoding.UTF8.GetBytes("[fake template zip]");
        var fakeDownloader = new CountingDownloader(bytes);

        var fetcher = new TemplateFetcher(env.TempRoot, fakeDownloader, env.Logger);

        var first = await fetcher.FetchAsync("v0.1.0-ue5.7-scaffold", CancellationToken.None);
        Assert.False(first.FromCache);
        Assert.Equal(1, fakeDownloader.CallCount);
        Assert.Equal(ComputeHash(bytes), first.Sha256);
        Assert.True(File.Exists(first.ZipPath));
        Assert.True(File.Exists(first.ZipPath + ".sha256"));

        // Second call: same fetcher, same tag → cache hit, no HTTP.
        var second = await fetcher.FetchAsync("v0.1.0-ue5.7-scaffold", CancellationToken.None);
        Assert.True(second.FromCache);
        Assert.Equal(1, fakeDownloader.CallCount); // unchanged
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.ZipPath, second.ZipPath);
    }

    [Fact]
    public async Task TamperedCache_RetriggersDownload()
    {
        using var env = new TestEnv();
        var goodBytes = Encoding.UTF8.GetBytes("[good zip]");
        var fakeDownloader = new CountingDownloader(goodBytes);
        var fetcher = new TemplateFetcher(env.TempRoot, fakeDownloader, env.Logger);

        var first = await fetcher.FetchAsync("vX", CancellationToken.None);
        Assert.False(first.FromCache);

        // Tamper with the cached zip after the fact: write a different
        // payload but leave the .sha256 sidecar pointing at the old hash.
        File.WriteAllBytes(first.ZipPath, Encoding.UTF8.GetBytes("[tampered]"));

        var second = await fetcher.FetchAsync("vX", CancellationToken.None);
        Assert.False(second.FromCache); // had to redownload
        Assert.Equal(2, fakeDownloader.CallCount);
        // Final on-disk content matches the trusted bytes again.
        Assert.Equal(goodBytes, await File.ReadAllBytesAsync(second.ZipPath));
    }

    [Fact]
    public async Task TemplateNotFoundException_BubblesUpFromDownloader()
    {
        using var env = new TestEnv();
        var notFoundDownloader = new ThrowingDownloader(
            new TemplateNotFoundException("404 from fake gh"));
        var fetcher = new TemplateFetcher(env.TempRoot, notFoundDownloader, env.Logger);

        await Assert.ThrowsAsync<TemplateNotFoundException>(() =>
            fetcher.FetchAsync("vGhost", CancellationToken.None));
    }

    private static string ComputeHash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class CountingDownloader : TemplateFetcher.ITemplateDownloader
    {
        private readonly byte[] _bytes;
        public int CallCount { get; private set; }

        public CountingDownloader(byte[] bytes) { _bytes = bytes; }

        public Task<byte[]> DownloadAsync(string url, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_bytes);
        }
    }

    private sealed class ThrowingDownloader : TemplateFetcher.ITemplateDownloader
    {
        private readonly Exception _ex;
        public ThrowingDownloader(Exception ex) { _ex = ex; }
        public Task<byte[]> DownloadAsync(string url, CancellationToken ct) =>
            Task.FromException<byte[]>(_ex);
    }
}
