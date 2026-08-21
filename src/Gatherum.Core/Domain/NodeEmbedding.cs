using Pgvector;

namespace Gatherum.Core.Domain;

/// <summary>One passage of a node's text and the vector it embeds to. A node gets many:
/// a single vector per node averages an hour-long transcript into a haze that is near
/// everything and close to nothing, while a passage keeps its own subject and can say
/// which part of a long page answered the search.</summary>
public class NodeEmbedding
{
    public Guid Id { get; init; }
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }

    /// <summary>Position in the node's text, so a result can be read in reading order.</summary>
    public int Ordinal { get; set; }

    public required string Text { get; set; }

    /// <summary>SHA-256 of the exact text embedded, matched together with
    /// <see cref="Model"/>. A vector belongs to those two things and nothing else, so an
    /// unchanged passage of an edited page — or the same passage in a re-uploaded file —
    /// is looked up rather than re-earned.</summary>
    public required string Hash { get; init; }

    public required Vector Embedding { get; set; }

    public required string Model { get; init; }
}
