using System.Text.Json.Nodes;

using Serilog;

using PRISM.Visualiser.Orchestrator.Converters.FromOrbit;
using PRISM.Visualiser.Orchestrator.Models;
using PRISM.Visualiser.Orchestrator.OrbitApi;

namespace PRISM.Visualiser.Orchestrator.Pipeline;

/// <summary>
/// BUILD.md Phase 1 — ORBIT receive pipeline. Given a project +
/// version, resolves the version → root object → full object tree
/// (cache-first; HTTP only on miss) and converts the tree into the
/// in-memory <see cref="StagedScene"/> the glTF writer consumes.
///
/// <para>
/// The pipeline runs in three deterministic passes so each pass can
/// be unit-tested in isolation:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Resolve version</b> — single <c>IOrbitApi.GetVersionAsync</c> call.
///   </description></item>
///   <item><description>
///     <b>Traverse object tree</b> — depth-first, parallel HTTP up to
///     <see cref="MaxConcurrentObjectFetches"/>; cache hits are free.
///   </description></item>
///   <item><description>
///     <b>Pre-resolve blobs</b> — every <c>@blob:HASH</c> reference
///     in any RenderMaterial body fans out through
///     <see cref="BlobDownloader"/> in parallel.
///   </description></item>
///   <item><description>
///     <b>Convert</b> — synchronous tree walk dispatched through
///     <see cref="IFromOrbitConverter"/>s + an inline collection
///     handler, producing the final <see cref="StagedScene"/>.
///   </description></item>
/// </list>
///
/// All HTTP IO goes through <see cref="IOrbitApi"/>; tests inject a
/// hand-rolled mock that returns synthetic JSON.
/// </summary>
public sealed class OrbitReceivePipeline
{
    /// <summary>Plan §Phase 1.2 — max parallel HTTP object fetches.</summary>
    public const int MaxConcurrentObjectFetches = 8;

    private readonly IOrbitApi _api;
    private readonly ContentAddressedCache _objectCache;
    private readonly BlobDownloader _blobDownloader;
    private readonly UnknownObjectSink _unknowns;
    private readonly ILogger _log;
    private readonly IReadOnlyList<IFromOrbitConverter> _converters;

    public OrbitReceivePipeline(
        IOrbitApi api,
        ContentAddressedCache objectCache,
        BlobDownloader blobDownloader,
        UnknownObjectSink unknowns,
        ILogger log,
        IReadOnlyList<IFromOrbitConverter>? converters = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _objectCache = objectCache ?? throw new ArgumentNullException(nameof(objectCache));
        _blobDownloader = blobDownloader ?? throw new ArgumentNullException(nameof(blobDownloader));
        _unknowns = unknowns ?? throw new ArgumentNullException(nameof(unknowns));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _converters = converters ?? BuildDefaultConverters();
    }

    /// <summary>
    /// Default converter chain: mesh → material → data-object →
    /// fallback. Order matters; the dispatch loop returns the first
    /// converter whose <see cref="IFromOrbitConverter.CanConvert"/>
    /// returns true.
    /// </summary>
    public static IReadOnlyList<IFromOrbitConverter> BuildDefaultConverters()
    {
        var mesh = new MeshConverter();
        return new IFromOrbitConverter[]
        {
            mesh,
            new MaterialConverter(),
            new DataObjectConverter(mesh),
            new FallbackConverter(),
        };
    }

    /// <summary>
    /// Run the full receive: resolve, traverse, convert. Returns the
    /// in-memory <see cref="StagedScene"/> ready for the glTF writer.
    /// </summary>
    public async Task<StagedScene> ReceiveAsync(
        string projectId,
        string versionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        _log.Information("receive: project={ProjectId} version={VersionId}", projectId, versionId);

        // 1. Resolve version → root object id.
        var version = await _api.GetVersionAsync(projectId, versionId, ct).ConfigureAwait(false);

        // 2. Traverse the object tree (cache-first, parallel-on-miss).
        var objects = await TraverseObjectsAsync(projectId, version.RootObjectId, ct)
            .ConfigureAwait(false);
        _log.Information("receive: traversed {Count} unique objects", objects.Count);

        // 3. Pre-resolve every @blob:HASH reference inside any
        //    RenderMaterial body (detached or inline).
        var blobHashes = CollectBlobHashes(objects.Values).ToArray();
        var blobPaths = blobHashes.Length > 0
            ? await _blobDownloader.ResolveManyAsync(projectId, blobHashes, ct).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 4. Build conversion context and convert.
        var materialRegistry = new Dictionary<string, StagedMaterial>(StringComparer.Ordinal);
        var ctx = new ConversionContext
        {
            ProjectId = projectId,
            LayerPath = string.Empty,
            ObjectsById = objects,
            BlobPaths = blobPaths,
            Unknowns = _unknowns,
            Logger = _log,
        };

        // Convert standalone (detached) RenderMaterials first so meshes
        // can resolve their refs into the registry without ordering
        // surprises.
        var materialConverter = _converters.OfType<MaterialConverter>().FirstOrDefault()
            ?? new MaterialConverter();
        foreach (var obj in objects.Values)
        {
            if (obj.SpeckleType != MaterialConverter.OrbitTypeName) continue;
            var staged = (StagedMaterial)materialConverter.Convert(obj, ctx);
            materialRegistry[staged.SourceObjectId] = staged;
        }

        // Inline materials live on a mesh's `renderMaterial` field as
        // a full body. Register one StagedMaterial per unique mesh
        // id (the mesh converter resolves to the same synthetic id
        // when emitting the StagedMesh.MaterialId).
        foreach (var obj in objects.Values)
        {
            if (obj.SpeckleType != MeshConverter.OrbitTypeName) continue;
            if (obj.Raw["renderMaterial"] is not JsonObject inlineBody) continue;
            if (inlineBody.ContainsKey("referencedId")) continue; // ref stub; handled above
            var inlineId = MeshConverter.InlineMaterialId(obj.Id);
            var staged = materialConverter.ConvertInline(inlineId, inlineBody, ctx);
            materialRegistry[inlineId] = staged;
        }

        // Walk the tree from the root.
        var root = objects[version.RootObjectId];
        var rootNode = ConvertNode(root, ctx, isRoot: true);
        if (rootNode is not StagedCollection rootCollection)
        {
            // Pathological case: the version's root is not a Collection.
            // Wrap in a synthetic root so downstream consumers always
            // see a Collection at the top.
            rootCollection = new StagedCollection(
                SourceObjectId: root.Id,
                SpeckleType: root.SpeckleType,
                Name: "root",
                LayerPath: string.Empty,
                Children: new[] { rootNode });
        }

        var unknowns = CollectUnknowns(rootCollection).ToArray();
        return new StagedScene(version, rootCollection, materialRegistry, unknowns);
    }

    // ----------------------------------------------------------------
    // Pass 2: object traversal
    // ----------------------------------------------------------------

    private async Task<IReadOnlyDictionary<string, OrbitObject>> TraverseObjectsAsync(
        string projectId, string rootId, CancellationToken ct)
    {
        // Each id is fetched at most once; the kicked-off Task<OrbitObject>
        // is stashed in `inFlight` so a second BFS edge that points to
        // the same id de-dupes onto the existing Task instead of
        // launching a duplicate HTTP request. Bounded parallelism is
        // enforced by `gate`; child enumeration only happens after the
        // parent's fetch completes (so we never enqueue refs we
        // discovered nothing about).
        using var gate = new SemaphoreSlim(MaxConcurrentObjectFetches, MaxConcurrentObjectFetches);
        var inFlight = new Dictionary<string, Task<OrbitObject>>(StringComparer.Ordinal);
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var fetched = new Dictionary<string, OrbitObject>(StringComparer.Ordinal);

        Task<OrbitObject> Schedule(string id)
        {
            return Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var json = await GetObjectJsonAsync(projectId, id, ct).ConfigureAwait(false);
                    var parsed = OrbitObject.Parse(json);
                    // Some object payloads omit `id` from the body
                    // because the id IS the URL path. Stamp it in
                    // before returning so downstream lookups work.
                    return string.IsNullOrEmpty(parsed.Id)
                        ? new OrbitObject(id, parsed.SpeckleType, parsed.Name, parsed.Raw)
                        : parsed;
                }
                finally { gate.Release(); }
            }, ct);
        }

        inFlight[rootId] = Schedule(rootId);

        while (true)
        {
            var pending = inFlight
                .Where(kv => !processed.Contains(kv.Key))
                .ToArray();
            if (pending.Length == 0) break;

            var winner = await Task.WhenAny(pending.Select(kv => kv.Value)).ConfigureAwait(false);
            var winnerId = pending.First(kv => kv.Value == winner).Key;
            processed.Add(winnerId);
            var obj = await winner.ConfigureAwait(false); // surfaces exceptions
            fetched[winnerId] = obj;
            foreach (var childRef in obj.EnumerateReferenceIds())
            {
                if (!inFlight.ContainsKey(childRef))
                {
                    inFlight[childRef] = Schedule(childRef);
                }
            }
        }

        return fetched;
    }

    /// <summary>
    /// Cache-first object fetch. Returns the JSON body of the object
    /// with content hash <paramref name="objectId"/>.
    /// </summary>
    public async Task<string> GetObjectJsonAsync(
        string projectId, string objectId, CancellationToken ct)
    {
        var cached = await _objectCache.TryReadObjectAsync(objectId, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            _log.Verbose("object cache hit id={ObjectId}", objectId);
            return cached;
        }

        _log.Information("object fetch id={ObjectId}", objectId);
        await using var stream = await _api
            .GetObjectAsync(projectId, objectId, ct)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        await _objectCache.WriteObjectAsync(objectId, json, ct).ConfigureAwait(false);
        return json;
    }

    // ----------------------------------------------------------------
    // Pass 3: blob hash collection
    // ----------------------------------------------------------------

    private static IEnumerable<string> CollectBlobHashes(IEnumerable<OrbitObject> objects)
    {
        foreach (var obj in objects)
        {
            if (obj.SpeckleType == MaterialConverter.OrbitTypeName)
            {
                foreach (var h in MaterialConverter.EnumerateBlobHashes(obj.Raw))
                    yield return h;
            }
            // Inline materials on meshes also contribute blob refs.
            if (obj.SpeckleType == MeshConverter.OrbitTypeName
                && obj.Raw["renderMaterial"] is JsonObject inline
                && !inline.ContainsKey("referencedId"))
            {
                foreach (var h in MaterialConverter.EnumerateBlobHashes(inline))
                    yield return h;
            }
        }
    }

    // ----------------------------------------------------------------
    // Pass 4: tree → StagedNode conversion
    // ----------------------------------------------------------------

    /// <summary>
    /// Speckle collection types — these flow through the recursive
    /// child handler instead of the per-leaf converter dispatch.
    /// </summary>
    private static readonly HashSet<string> CollectionTypes = new(StringComparer.Ordinal)
    {
        "Speckle.Core.Models.Collections.Collection",
        "Objects.Other.Collections.Collection",
    };

    private StagedNode ConvertNode(OrbitObject obj, ConversionContext ctx, bool isRoot)
    {
        if (CollectionTypes.Contains(obj.SpeckleType))
        {
            var name = obj.Name ?? (isRoot ? "root" : obj.Id);
            var childCtx = isRoot
                ? new ConversionContext
                {
                    ProjectId = ctx.ProjectId,
                    LayerPath = name,
                    ObjectsById = ctx.ObjectsById,
                    BlobPaths = ctx.BlobPaths,
                    Unknowns = ctx.Unknowns,
                    Logger = ctx.Logger,
                }
                : ctx.WithLayer(name);

            var children = new List<StagedNode>();
            foreach (var childId in obj.EnumerateReferenceIds())
            {
                if (!ctx.ObjectsById.TryGetValue(childId, out var childObj))
                {
                    ctx.Logger.Warning(
                        "collection {ParentId} references missing child {ChildId}",
                        obj.Id, childId);
                    continue;
                }
                // Skip RenderMaterials when they appear as collection
                // children — they're indexed in StagedScene.Materials,
                // not embedded in the tree.
                if (childObj.SpeckleType == MaterialConverter.OrbitTypeName) continue;
                children.Add(ConvertNode(childObj, childCtx, isRoot: false));
            }

            return new StagedCollection(
                SourceObjectId: obj.Id,
                SpeckleType: obj.SpeckleType,
                Name: name,
                LayerPath: childCtx.LayerPath,
                Children: children);
        }

        // Leaf: pick the first converter that claims it.
        foreach (var c in _converters)
        {
            if (c.CanConvert(obj))
            {
                return c.Convert(obj, ctx);
            }
        }
        // FallbackConverter.CanConvert always returns true; reaching
        // this branch implies a misconfigured converter list.
        throw new InvalidOperationException(
            $"No converter claimed object id={obj.Id} type={obj.SpeckleType}.");
    }

    private static IEnumerable<StagedUnknown> CollectUnknowns(StagedNode node)
    {
        if (node is StagedUnknown u) yield return u;
        if (node is StagedCollection coll)
        {
            foreach (var child in coll.Children)
            {
                foreach (var nested in CollectUnknowns(child)) yield return nested;
            }
        }
    }
}
