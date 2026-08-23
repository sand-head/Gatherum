using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;

namespace Gatherum.Core.Services;

/// <summary>Ownership, the effective-public flag, and the access closure — three columns
/// and one join, no ancestor walk. <see cref="AccessService"/> keeps all three true.</summary>
public class DefaultNodeAuthorizer : INodeAuthorizer
{
    public IQueryable<Node> VisibleTo(IQueryable<Node> nodes, Guid? userId) =>
        userId is { } id
            ? nodes.Where(n => n.EffectivePublic || n.OwnerId == id
                || n.AccessEntries.Any(e => e.UserId == id))
            : nodes.Where(n => n.EffectivePublic);

    public bool CanSee(Node node, Guid? userId) =>
        node.EffectivePublic
        || (userId is { } id && (node.OwnerId == id || node.AccessEntries.Any(e => e.UserId == id)));

    public bool CanEdit(Node node, Guid? userId) =>
        userId is { } id
        && (node.OwnerId == id
            || node.AccessEntries.Any(e => e.UserId == id && e.Role == AccessRole.Editor));
}
