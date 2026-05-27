using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.OrbitApi;

/// <summary>
/// Narrow surface every layer above the receive pipeline talks to.
/// Phase C tests inject a hand-rolled mock; Phase E swaps in the real
/// <see cref="HttpOrbitApi"/>. Keeping the surface minimal makes both
/// trivial to wire.
/// </summary>
public interface IOrbitApi
{
    /// <summary>
    /// Resolve a single <see cref="VersionDescriptor"/>. Hits
    /// <c>GET /api/v1/projects/{projectId}/versions/{versionId}</c>.
    /// </summary>
    Task<VersionDescriptor> GetVersionAsync(
        string projectId,
        string versionId,
        CancellationToken ct);

    /// <summary>
    /// Fetch a single ORBIT object by content hash. The returned
    /// stream is the raw JSON body of the object (children replaced
    /// inline by <c>{referencedId, speckle_type:"reference"}</c>
    /// stubs). Caller owns the stream.
    /// </summary>
    Task<Stream> GetObjectAsync(
        string projectId,
        string objectId,
        CancellationToken ct);

    /// <summary>
    /// Fetch a binary blob by content hash. Used for textures and
    /// raw attachments. Caller owns the stream.
    /// </summary>
    Task<Stream> GetBlobAsync(
        string projectId,
        string blobHash,
        CancellationToken ct);
}
