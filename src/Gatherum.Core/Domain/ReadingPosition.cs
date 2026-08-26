namespace Gatherum.Core.Domain;

/// <summary>Where a reader stopped in a book: the chapter that was on the desk and how
/// far through it they were. One row per reader per node — the ribbon, not the book.
/// Deliberately a table-only exception to "the filesystem is the system of record"
/// (see DECISIONS.md): it is nobody's content, it is not derivable from the
/// directories, and losing it costs exactly a page number.</summary>
public class ReadingPosition
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public Guid UserId { get; init; }
    public User? User { get; init; }

    public int Chapter { get; set; }

    /// <summary>How far through the chapter, 0..1. A page number would renumber with
    /// every window size and font; a fraction survives them all.</summary>
    public double Progress { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
