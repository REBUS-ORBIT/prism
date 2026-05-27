using System.Text;
using System.Text.Json.Nodes;

using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.OrbitApi;

namespace PRISM.Visualiser.Orchestrator.Tests.TestHelpers;

/// <summary>
/// Hand-rolled <see cref="IOrbitApi"/> mock for the Phase C smoke
/// tests. No live HTTP, no Moq — every call returns a synthetic
/// response from the in-memory dictionaries the test seeded.
///
/// Tracks per-method call counts so tests can assert "second receive
/// hit the cache" (call counts unchanged) without observing the cache
/// directory directly.
/// </summary>
public sealed class FakeOrbitApi : IOrbitApi
{
    private readonly Dictionary<string, VersionDescriptor> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public int VersionCalls { get; private set; }
    public int ObjectCalls { get; private set; }
    public int BlobCalls { get; private set; }

    public IReadOnlyDictionary<string, int> ObjectCallsById => _objectCallsById;
    private readonly Dictionary<string, int> _objectCallsById = new(StringComparer.Ordinal);

    public FakeOrbitApi RegisterVersion(VersionDescriptor v)
    {
        var key = $"{v.ProjectId}/{v.VersionId}";
        _versions[key] = v;
        return this;
    }

    public FakeOrbitApi RegisterObject(string objectId, JsonObject body)
    {
        _objects[objectId] = body.ToJsonString();
        return this;
    }

    public FakeOrbitApi RegisterObject(string objectId, string json)
    {
        _objects[objectId] = json;
        return this;
    }

    public FakeOrbitApi RegisterBlob(string hash, byte[] bytes)
    {
        _blobs[hash] = bytes;
        return this;
    }

    public Task<VersionDescriptor> GetVersionAsync(
        string projectId, string versionId, CancellationToken ct)
    {
        VersionCalls++;
        var key = $"{projectId}/{versionId}";
        if (!_versions.TryGetValue(key, out var v))
            throw new OrbitApiException($"Fake API has no version registered for {key}");
        return Task.FromResult(v);
    }

    public Task<Stream> GetObjectAsync(
        string projectId, string objectId, CancellationToken ct)
    {
        ObjectCalls++;
        _objectCallsById[objectId] = _objectCallsById.GetValueOrDefault(objectId) + 1;
        if (!_objects.TryGetValue(objectId, out var json))
            throw new OrbitApiException($"Fake API has no object registered for id {objectId}");
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    public Task<Stream> GetBlobAsync(
        string projectId, string blobHash, CancellationToken ct)
    {
        BlobCalls++;
        if (!_blobs.TryGetValue(blobHash, out var bytes))
            throw new OrbitApiException($"Fake API has no blob registered for hash {blobHash}");
        return Task.FromResult<Stream>(new MemoryStream(bytes));
    }
}
