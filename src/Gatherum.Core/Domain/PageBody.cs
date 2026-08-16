namespace Gatherum.Core.Domain;

/// <summary>The rich-text body of a page node, stored as TipTap document JSON.</summary>
public class PageBody
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public required string Doc { get; set; }
}
