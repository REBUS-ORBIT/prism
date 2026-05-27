namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// Resolved metadata for a single ORBIT version. Phase C only needs
/// enough to seed the receive pipeline (project / model / version ids
/// + the root object id of the version's content tree); future phases
/// will hang author / timestamp / message on this record without
/// changing the constructor surface.
///
/// The shape mirrors the response of
/// <c>GET /api/v1/projects/{projectId}/versions/{versionId}</c> so the
/// HTTP transport layer can deserialise into this record directly.
/// </summary>
public sealed record VersionDescriptor(
    string ProjectId,
    string ModelId,
    string VersionId,
    string RootObjectId,
    string? Message = null,
    string? AuthorId = null,
    DateTimeOffset? CreatedAt = null);
