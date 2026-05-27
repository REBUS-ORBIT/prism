using Serilog;

using PRISM.Visualiser.Orchestrator.Models;

namespace PRISM.Visualiser.Orchestrator.Converters.FromOrbit;

/// <summary>
/// Per-conversion ambient context. Constructed once at the start of
/// <c>OrbitReceivePipeline.ReceiveAsync</c> and threaded through every
/// converter invocation. Mutable bookkeeping (the unknown-object
/// sidecar log, the material registry) lives here so converters
/// don't need to take five constructor parameters each.
/// </summary>
public sealed class ConversionContext
{
    /// <summary>ORBIT project the receive is rooted in.</summary>
    public required string ProjectId { get; init; }

    /// <summary>
    /// Layer path of the parent collection, e.g. <c>"Root::Floor::Walls"</c>.
    /// Empty for the root collection itself; converters propagate this
    /// downward when recursing into nested collections.
    /// </summary>
    public required string LayerPath { get; init; }

    /// <summary>
    /// All ORBIT objects fetched during the receive's traversal phase,
    /// keyed by content hash. Converters use this to resolve inline
    /// references (e.g. a mesh's <c>renderMaterial</c> stub).
    /// </summary>
    public required IReadOnlyDictionary<string, OrbitObject> ObjectsById { get; init; }

    /// <summary>
    /// Pre-resolved blob hash → on-disk path map. Material conversion
    /// reads this; the BlobDownloader populated it before the
    /// converter pass started.
    /// </summary>
    public required IReadOnlyDictionary<string, string> BlobPaths { get; init; }

    /// <summary>
    /// Sink for unknown-object sidecar lines. Writes go through
    /// <see cref="UnknownObjectSink"/> so tests can assert on the
    /// recorded entries without parsing the on-disk JSONL file.
    /// </summary>
    public required UnknownObjectSink Unknowns { get; init; }

    /// <summary>Per-run logger.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>Build a child context with an extended layer path.</summary>
    public ConversionContext WithLayer(string segment)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment);
        var path = string.IsNullOrEmpty(LayerPath) ? segment : $"{LayerPath}::{segment}";
        return new ConversionContext
        {
            ProjectId = ProjectId,
            LayerPath = path,
            ObjectsById = ObjectsById,
            BlobPaths = BlobPaths,
            Unknowns = Unknowns,
            Logger = Logger,
        };
    }
}
