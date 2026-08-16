using NpgsqlTypes;

namespace Gatherum.Core.Domain;

public enum NodeKind
{
    Page,
    File,
}

public class Node
{
    public Guid Id { get; init; }
    public NodeKind Kind { get; init; }
    public required string Title { get; set; }
    public Guid? ParentId { get; set; }
    public Node? Parent { get; set; }
    public int Position { get; set; }

    /// <summary>Marks this node as the root of a subtree visible only to its owner.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Owner of the nearest private ancestor (or self), denormalized so
    /// visibility is a single-column filter instead of an ancestor walk. Maintained
    /// by NodeService on privacy changes and moves.</summary>
    public Guid? PrivateToUserId { get; set; }
    public Guid OwnerId { get; init; }
    public User? Owner { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Plain text the node is findable by: tags plus body or extracted text.
    /// The title is weighted separately in the tsvector, so it is not repeated here.</summary>
    public string SearchText { get; set; } = "";

    public NpgsqlTsVector SearchVector { get; init; } = null!;

    public List<Node> Children { get; init; } = [];
    public PageBody? Page { get; set; }
    public FileBody? File { get; set; }
    public List<NodeTag> Tags { get; init; } = [];
    public List<NodeLink> OutboundLinks { get; init; } = [];
    public List<NodeLink> InboundLinks { get; init; } = [];
    public List<Revision> Revisions { get; init; } = [];
}
