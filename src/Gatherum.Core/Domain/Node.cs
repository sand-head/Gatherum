using NpgsqlTypes;

namespace Gatherum.Core.Domain;

/// <summary>How a node presents. Page and File are a lens over
/// <see cref="Node.MediaType"/>; Category is the one that is stored, because a category
/// page is a Markdown file in every other respect and nothing about its bytes says what
/// it is for.</summary>
public enum NodeKind
{
    Page,
    File,
    Category,
}

public class Node
{
    public Guid Id { get; init; }
    public required string Title { get; set; }

    /// <summary>Media type of the current file version, denormalized here so tree
    /// rendering and kind filtering never need a join.</summary>
    public string MediaType { get; set; } = MediaTypes.Binary;

    /// <summary>Whether this node is a subject rather than a page about one. A category
    /// is an ordinary Markdown page that has been marked as one: it has a body to say
    /// what belongs in it, a history, backlinks, and its own categories — which are the
    /// categories it is nested under. Carried on disk in the sidecar, so the taxonomy
    /// survives the database like everything else does.</summary>
    public bool IsCategory { get; set; }

    public NodeKind Kind => IsCategory
        ? NodeKind.Category
        : MediaType == MediaTypes.Markdown ? NodeKind.Page : NodeKind.File;

    public Guid? ParentId { get; set; }
    public Node? Parent { get; set; }
    public int Position { get; set; }

    /// <summary>What this node declares about who may reach it. Private unless its owner
    /// says otherwise, which is also what a node with no metadata at all means.</summary>
    public AccessMode Access { get; set; } = AccessMode.Private;

    /// <summary>Whether an ancestor's access carries down to here. Access is additive
    /// downward — sharing a directory shares what is in it — and this is the escape hatch
    /// for a subtree that has to be tighter than the one containing it.</summary>
    public bool InheritAccess { get; set; } = true;

    /// <summary>How far this node reaches once inheritance has been applied, denormalized
    /// from <see cref="Access"/> up the ancestry so both visibility questions stay
    /// single-column filters. Written only by <see cref="Services.AccessService"/>.</summary>
    public NodeReach Reach { get; set; }

    /// <summary>Whether every signed-in reader may enumerate this node, derived from
    /// <see cref="Access"/> up the ancestry exactly as <see cref="Reach"/> is. The second
    /// axis of visibility rather than a rung of the first — see
    /// <see cref="AccessModes.ListsToSignedIn"/> for why it cannot be one. Written only by
    /// <see cref="Services.AccessService"/>.</summary>
    public bool ListedToSignedIn { get; set; }

    /// <summary>Ownership is the path: whoever owns the root directory this node was
    /// found under owns the node, and nothing recorded anywhere may disagree. That is
    /// what lets ownership survive the database — it is read off the layout, never
    /// stored as an opinion about it.</summary>
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>Where the bytes live, relative to the owner's root, with '/' separators.
    /// Empty for a node that is only a place in the tree. This is the node's address in
    /// the system of record; the database merely indexes it.</summary>
    public string RelativePath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Plain text the node is findable by: category paths, filename,
    /// description, and extracted text. The title is weighted separately in the
    /// tsvector, so it is not repeated here.</summary>
    public string SearchText { get; set; } = "";

    public NpgsqlTsVector SearchVector { get; init; } = null!;

    /// <summary>Fingerprint of everything a node is embedded from, computed by the
    /// database. Compared against <see cref="EmbeddedFingerprint"/>, it is the whole of
    /// how the embedding worker knows what to do: no flag to set, no queue to remember,
    /// and a category rename that rewrites a hundred nodes' search text marks all
    /// hundred without knowing this exists.</summary>
    public string TextFingerprint { get; init; } = "";

    /// <summary>The fingerprint the current embeddings were made from; empty until a
    /// node has ever been embedded, and stale exactly when it differs from the one
    /// above.</summary>
    public string EmbeddedFingerprint { get; set; } = "";

    public List<Node> Children { get; init; } = [];
    public List<NodeGrant> Grants { get; init; } = [];
    public List<NodeAccessEntry> AccessEntries { get; init; } = [];
    public FileBody? File { get; set; }
    /// <summary>The categories this node is filed under. On a category node these are
    /// the categories it is nested under — its parents in the taxonomy.</summary>
    public List<NodeCategory> Categories { get; init; } = [];

    /// <summary>What is filed under this node, when it is a category. Empty for
    /// everything else.</summary>
    public List<NodeCategory> Members { get; init; } = [];
    public List<NodeLink> OutboundLinks { get; init; } = [];
    public List<NodeLink> InboundLinks { get; init; } = [];
    public List<NodeEmbedding> Embeddings { get; init; } = [];
}
