using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using Microsoft.Extensions.Options;

namespace Gatherum.Core.Services;

/// <summary>Ownership, the effective-public flag, and the access closure — three columns
/// and one join, no ancestor walk. <see cref="AccessService"/> keeps all three true.
///
/// The instance-wide public switch is honoured here rather than at the edge, because here
/// is the one place every visibility-sensitive query passes through: turning it off hides
/// public nodes from search, tree, categories and direct reads in the same breath, and
/// without rewriting a single thing somebody recorded on disk.</summary>
public class DefaultNodeAuthorizer(IOptions<GatherumOptions> options) : INodeAuthorizer
{
    private bool AllowPublic => options.Value.Sharing.AllowPublic;

    public IQueryable<Node> VisibleTo(IQueryable<Node> nodes, Guid? userId)
    {
        var allowPublic = AllowPublic;
        return userId is { } id
            ? nodes.Where(n => (allowPublic && n.EffectivePublic) || n.OwnerId == id
                || n.AccessEntries.Any(e => e.UserId == id))
            : nodes.Where(n => allowPublic && n.EffectivePublic);
    }

    public bool CanSee(Node node, Guid? userId) =>
        (AllowPublic && node.EffectivePublic)
        || (userId is { } id && (node.OwnerId == id || node.AccessEntries.Any(e => e.UserId == id)));

    public bool CanEdit(Node node, Guid? userId) =>
        userId is { } id
        && (node.OwnerId == id
            || node.AccessEntries.Any(e => e.UserId == id && e.Role == AccessRole.Editor));
}
