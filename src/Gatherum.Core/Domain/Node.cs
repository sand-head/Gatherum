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

    /// <summary>What this node declares about who may reach it. Private unless its owner
    /// says otherwise, which is also what a node with no metadata at all means.</summary>
    public AccessMode Access { get; set; } = AccessMode.Private;

    /// <summary>Whether an ancestor's access carries down to here. Access is additive
    /// downward — sharing a directory shares what is in it — and this is the escape hatch
    /// for a subtree that has to be tighter than the one containing it.</summary>
    public bool InheritAccess { get; set; } = true;

    /// <summary>Reachable without signing in, once inheritance has been applied.
    /// Denormalized from <see cref="Access"/> up the ancestry so an anonymous request is
    /// a single-column filter. Written only by <see cref="Services.AccessService"/>.</summary>
    public bool EffectivePublic { get; set; }

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
    public List<NodeCategory> Categories { get; init; } = [];
    public List<NodeLink> OutboundLinks { get; init; } = [];
    public List<NodeLink> InboundLinks { get; init; } = [];
    public List<NodeEmbedding> Embeddings { get; init; } = [];
}
