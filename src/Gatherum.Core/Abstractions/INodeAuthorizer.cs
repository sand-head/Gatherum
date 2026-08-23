using Gatherum.Core.Domain;

namespace Gatherum.Core.Abstractions;

/// <summary>The single gate for node visibility, and the only door anonymous readers
/// come through. Every visibility-sensitive query funnels here rather than spelling the
/// rule again, so widening it to admit unauthenticated callers admits them correctly
/// everywhere at once — tree, search, categories, similar, backlinks, title resolution —
/// instead of one audited endpoint at a time. Keep it the only door.</summary>
public interface INodeAuthorizer
{
    /// <param name="userId">The signed-in user, or <c>null</c> for an anonymous request,
    /// which sees public nodes and nothing else.</param>
    IQueryable<Node> VisibleTo(IQueryable<Node> nodes, Guid? userId);

    /// <summary>Requires <see cref="Node.AccessEntries"/> to be loaded for a grantee to
    /// be seen; owner and public reads need nothing loaded.</summary>
    bool CanSee(Node node, Guid? userId);

    /// <summary>Anonymous callers can never write, whatever a node's access says:
    /// <see cref="AccessMode.Public"/> is a publishing gesture, not an invitation.</summary>
    bool CanEdit(Node node, Guid? userId);
}
