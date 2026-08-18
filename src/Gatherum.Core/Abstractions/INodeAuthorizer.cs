using Gatherum.Core.Domain;

namespace Gatherum.Core.Abstractions;

/// <summary>The single gate for node visibility. Today the only rule is the
/// private-subtree flag; richer ACLs replace this implementation, not its callers.</summary>
public interface INodeAuthorizer
{
    IQueryable<Node> VisibleTo(IQueryable<Node> nodes, Guid userId);
    bool CanSee(Node node, Guid userId);
}
