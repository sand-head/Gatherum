namespace Gatherum.Core.Domain;

/// <summary>A directed link from one node's body (an @-mention, embedded image, or
/// description mention) to another node. Backlinks are this relation read in reverse.</summary>
public class NodeLink
{
    public Guid SourceId { get; init; }
    public Node? Source { get; init; }
    public Guid TargetId { get; init; }
    public Node? Target { get; init; }
}
