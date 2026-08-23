using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using Microsoft.Extensions.Options;

namespace Gatherum.Core.Services;

/// <summary>Two questions that look like one until a node is unlisted.
///
/// <see cref="VisibleTo"/> answers "what may this caller enumerate" — the tree, search,
/// category pages, backlinks, wiki-link resolution. <see cref="CanSee"/> answers "may this
/// caller reach this particular node", which is what a direct link asks. For every other
/// access mode the two agree; unlisted is the one that says yes to the second and no to
/// the first, and its id is what stands in for permission.
///
/// Both are ownership, one column and one join — no ancestor walk. The instance-wide
/// public switch is honoured here rather than at the edge, because here is the one place
/// every visibility-sensitive query passes through.</summary>
public class DefaultNodeAuthorizer(IOptions<GatherumOptions> options) : INodeAuthorizer
{
    private NodeReach Ceiling => options.Value.Sharing.AllowPublic ? NodeReach.Listed : NodeReach.None;

    public IQueryable<Node> VisibleTo(IQueryable<Node> nodes, Guid? userId)
    {
        var ceiling = Ceiling;
        return userId is { } id
            ? nodes.Where(n => (n.Reach == NodeReach.Listed && ceiling >= NodeReach.Listed)
                || n.OwnerId == id || n.AccessEntries.Any(e => e.UserId == id))
            : nodes.Where(n => n.Reach == NodeReach.Listed && ceiling >= NodeReach.Listed);
    }

    public bool CanSee(Node node, Guid? userId) =>
        (node.Reach >= NodeReach.WithLink && Ceiling >= node.Reach)
        || (userId is { } id && (node.OwnerId == id || node.AccessEntries.Any(e => e.UserId == id)));

    public bool CanEdit(Node node, Guid? userId) =>
        userId is { } id
        && (node.OwnerId == id
            || node.AccessEntries.Any(e => e.UserId == id && e.Role == AccessRole.Editor));
}
