namespace PRISM.Visualiser.Orchestrator.Models;

/// <summary>
/// Top-level result of the receive pipeline. Wraps the staged tree
/// rooted at the version's root object plus a flat material index for
/// the glTF writer (which references materials by id, not by tree
/// position).
/// </summary>
public sealed record StagedScene(
    VersionDescriptor Version,
    StagedCollection Root,
    IReadOnlyDictionary<string, StagedMaterial> Materials,
    IReadOnlyList<StagedUnknown> Unknowns)
{
    /// <summary>
    /// Total mesh count anywhere in the tree. Cheap to recompute and
    /// avoids leaking a counter through every converter.
    /// </summary>
    public int CountMeshes() => CountKind(Root, n => n is StagedMesh);

    public int CountObjects() => CountKind(Root, _ => true);

    private static int CountKind(StagedNode node, Func<StagedNode, bool> predicate)
    {
        var count = predicate(node) ? 1 : 0;
        if (node is StagedCollection coll)
        {
            foreach (var child in coll.Children)
            {
                count += CountKind(child, predicate);
            }
        }
        return count;
    }
}
