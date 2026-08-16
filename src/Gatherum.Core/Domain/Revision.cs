namespace Gatherum.Core.Domain;

/// <summary>A snapshot of a page node's title and document, taken on every save.</summary>
public class Revision
{
    public Guid Id { get; init; }
    public Guid NodeId { get; init; }
    public int Number { get; init; }
    public required string Title { get; init; }
    public required string Doc { get; init; }
    public Guid AuthorId { get; init; }
    public User? Author { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
