namespace Gatherum.Client;

/// <summary>The sidebar's view of the tree, shared across a screen's interactive
/// components (one circuit or one WebAssembly runtime) so anything that changes the
/// hierarchy can refresh it for everyone showing it.</summary>
public sealed class TreeState(IAppData data)
{
    public IReadOnlyList<TreeNodeInfo> Nodes { get; private set; } = [];
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        Nodes = await data.GetTreeAsync();
        Changed?.Invoke();
    }

    public ILookup<Guid?, TreeNodeInfo> ByParent => Nodes.ToLookup(n => n.ParentId);

    /// <summary>Everything the viewer owns, from the top of their own tree down.</summary>
    public IEnumerable<TreeNodeInfo> OwnedRoots => Roots.Where(n => n.Owned);

    /// <summary>Things shared in from somebody else's tree. Ownership is the path, so
    /// these live under a parent the viewer usually cannot see — and a tree that only
    /// walked down from a null parent would never reach them at all.</summary>
    public IEnumerable<TreeNodeInfo> SharedRoots => Roots.Where(n => !n.Owned);

    /// <summary>Where drawing starts: a node whose parent is not itself visible. That is
    /// the null-parented tops of the viewer's own tree, and the highest visible node of
    /// anything shared in from someone else's.</summary>
    private IEnumerable<TreeNodeInfo> Roots
    {
        get
        {
            var visible = Nodes.Select(n => n.Id).ToHashSet();
            return Nodes
                .Where(n => n.ParentId is not { } parent || !visible.Contains(parent))
                .OrderBy(n => n.Position)
                .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase);
        }
    }
}
