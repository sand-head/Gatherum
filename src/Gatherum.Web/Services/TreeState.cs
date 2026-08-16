using Gatherum.Core.Services;

namespace Gatherum.Web.Services;

/// <summary>The sidebar's view of the tree, shared per circuit so any component that
/// changes the hierarchy can refresh it for the whole screen.</summary>
public sealed class TreeState(AppOperations ops)
{
    public IReadOnlyList<TreeNode> Nodes { get; private set; } = [];
    public event Action? Changed;

    public async Task RefreshAsync(Guid userId)
    {
        Nodes = await ops.Nodes(s => s.GetTreeAsync(userId));
        Changed?.Invoke();
    }

    public ILookup<Guid?, TreeNode> ByParent => Nodes.ToLookup(n => n.ParentId);
}
