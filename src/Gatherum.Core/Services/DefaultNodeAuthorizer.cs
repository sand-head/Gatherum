using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;

namespace Gatherum.Core.Services;

public class DefaultNodeAuthorizer : INodeAuthorizer
{
    public IQueryable<Node> VisibleTo(IQueryable<Node> nodes, Guid userId) =>
        nodes.Where(n => n.PrivateToUserId == null || n.PrivateToUserId == userId);

    public bool CanSee(Node node, Guid userId) =>
        node.PrivateToUserId is null || node.PrivateToUserId == userId;
}
