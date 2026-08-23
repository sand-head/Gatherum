namespace Gatherum.Core.Domain;

/// <summary>What a node declares about who may reach it. Absent a declaration a node is
/// <see cref="Private"/>: a directory Gatherum has never seen publishes nothing, which is
/// the same reason the default is safe and the reason a bare home directory just works.</summary>
public enum AccessMode
{
    /// <summary>The owner, and nobody else.</summary>
    Private,

    /// <summary>The owner plus whoever the grants name.</summary>
    Shared,

    /// <summary>Anyone on the internet, unauthenticated. A publishing gesture.</summary>
    Public,
}

/// <summary>What a grantee may do. The owner is always both and is never granted.</summary>
public enum AccessRole
{
    Reader,
    Editor,
}

/// <summary>One name on one node's access block — the owner's own declaration, read from
/// the manifest beneath their root. Inheritance turns these into
/// <see cref="NodeAccessEntry"/> rows; this is the statement, that is the closure.</summary>
public class NodeGrant
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public Guid UserId { get; init; }
    public User? User { get; init; }
    public AccessRole Role { get; set; }
}

/// <summary>Who can actually reach a node once inheritance has been applied, denormalized
/// so visibility stays one indexed predicate instead of an ancestor walk. Derived state:
/// <see cref="Services.AccessService"/> owns every row and nothing else writes here.</summary>
public class NodeAccessEntry
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public Guid UserId { get; init; }
    public AccessRole Role { get; set; }
}
