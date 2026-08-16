namespace Gatherum.Core.Domain;

/// <summary>Persisted CRDT state for a page's live-collaboration document.</summary>
public class YjsDoc
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public required byte[] State { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
