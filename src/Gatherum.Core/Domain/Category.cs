namespace Gatherum.Core.Domain;

/// <summary>A subject in the taxonomy. Categories nest — "Homelab/Podman" is a
/// subcategory of "Homelab" — and a node belongs to as many of them as its subject
/// demands, which is what separates them from the one place the node has in the tree.
/// Identity is <see cref="Path"/>, the normalized chain of names, because that is what
/// a writer types and a URL carries; <see cref="Name"/> keeps their capitalization.</summary>
public class Category
{
    public Guid Id { get; init; }

    /// <summary>The last segment as it was first written, for display.</summary>
    public required string Name { get; set; }

    /// <summary>Lowercase, slash-separated chain from a root category down to this one.
    /// Unique, and denormalized on purpose: a category's descendants are exactly the
    /// rows whose path starts with this one plus a slash.</summary>
    public required string Path { get; set; }

    public Guid? ParentId { get; set; }
    public Category? Parent { get; set; }

    public List<Category> Children { get; init; } = [];
    public List<NodeCategory> Nodes { get; init; } = [];
}

public class NodeCategory
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public Guid CategoryId { get; init; }
    public Category? Category { get; init; }
}
