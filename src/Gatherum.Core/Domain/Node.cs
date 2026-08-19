using NpgsqlTypes;

namespace Gatherum.Core.Domain;

/// <summary>How a node presents: a Page is simply a node whose file is Markdown.
/// The distinction is a lens over <see cref="Node.MediaType"/>, not stored state.</summary>
public enum NodeKind
{
    Page,
    File,
}

public class Node
{
    public Guid Id { get; init; }
    public required string Title { get; set; }

    /// <summary>Media type of the current file version, denormalized here so tree
    /// rendering and kind filtering never need a join.</summary>
    public string MediaType { get; set; } = MediaTypes.Binary;

    public NodeKind Kind => MediaType == MediaTypes.Markdown ? NodeKind.Page : NodeKind.File;

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

    /// <summary>Plain text the node is findable by: category paths, filename,
    /// description, and extracted text. The title is weighted separately in the
    /// tsvector, so it is not repeated here.</summary>
    public string SearchText { get; set; } = "";

    public NpgsqlTsVector SearchVector { get; init; } = null!;

    public List<Node> Children { get; init; } = [];
    public FileBody? File { get; set; }
    public List<NodeCategory> Categories { get; init; } = [];
    public List<NodeLink> OutboundLinks { get; init; } = [];
    public List<NodeLink> InboundLinks { get; init; } = [];
}
