namespace Gatherum.Core.Domain;

/// <summary>A node is about a subject. Both ends are nodes, because a category is a page:
/// the member can be any node, and <see cref="CategoryId"/> names a node whose
/// <see cref="Node.IsCategory"/> is set.
///
/// This is the taxonomy's <em>only</em> relation, and that is the point. A category filed
/// under another category is a subcategory — nesting is not a second mechanism, a path,
/// or a parent column, it is the same edge pointing at a category instead of at a page.
/// Which also means the taxonomy is a graph rather than a tree: "Podman" can sit under
/// "Homelab" and under "Containers" at once, the way an encyclopedia's index does and a
/// slash-separated path never could.</summary>
public class NodeCategory
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }

    /// <summary>The category node this membership files <see cref="NodeId"/> under.</summary>
    public Guid CategoryId { get; init; }
    public Node? Category { get; init; }
}
