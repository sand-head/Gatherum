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
}
