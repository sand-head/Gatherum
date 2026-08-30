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

    /// <summary>Anyone holding the link, and nobody else. Reachable without signing in,
    /// exactly like <see cref="Public"/>, but absent from every listing: the tree, search,
    /// category pages, backlinks, wiki-link resolution. Its id is the secret.
    ///
    /// This is the state that forces "what may you reach" and "what may you enumerate"
    /// apart. Everywhere else the two answers coincide.</summary>
    Unlisted,

    /// <summary>Anyone on the internet, unauthenticated, and listed. A publishing
    /// gesture.</summary>
    Public,

    /// <summary>Everyone this instance has authenticated, listed — and nobody else. The
    /// mode for a wiki shared by a group: one gesture instead of one grant per person,
    /// without the internet being invited.
    ///
    /// Declared last rather than in its conceptual place between <see cref="Shared"/> and
    /// <see cref="Unlisted"/> because the database stores this enum by ordinal, and
    /// inserting a value would silently renumber every access already recorded. The
    /// sidecar writes it by name and does not care; the rows do.</summary>
    Authenticated,
}

/// <summary>How far a node reaches past the people explicitly given access — its owner
/// and its grantees. Derived from <see cref="AccessMode"/> up the ancestry and stored on
/// the node, because it is what every visibility query filters on.
///
/// Ordered, so inheritance is a maximum and the two questions are comparisons:
/// enumerating needs <see cref="Listed"/>, reaching needs <see cref="WithLink"/>.
/// <see cref="AccessMode.Shared"/> has no place on this scale — naming a person is not
/// reach, and the grant closure carries it instead.</summary>
public enum NodeReach
{
    /// <summary>Nobody beyond the owner and the grantees.</summary>
    None,

    /// <summary>Anyone holding the id, and no listing anywhere reveals it.</summary>
    WithLink,

    /// <summary>Anyone, and it appears in trees, searches and category pages.</summary>
    Listed,
}

/// <summary>What a grantee may do. The owner is always both and is never granted.</summary>
public enum AccessRole
{
    Reader,
    Editor,
}

public static class AccessModes
{
    /// <summary>How far a declaration reaches on its own, before inheritance.
    /// <see cref="AccessMode.Shared"/> reaches nobody: it names people, and the grant
    /// closure is where naming people is recorded. Neither does
    /// <see cref="AccessMode.Authenticated"/>: this scale is what the *anonymous* internet
    /// gets, and admitting signed-in readers is <see cref="ListsToSignedIn"/> instead.</summary>
    public static NodeReach Reach(this AccessMode mode) => mode switch
    {
        AccessMode.Public => NodeReach.Listed,
        AccessMode.Unlisted => NodeReach.WithLink,
        _ => NodeReach.None,
    };

    /// <summary>Whether a declaration lets every signed-in reader enumerate the node.
    ///
    /// This is a second axis rather than another rung on <see cref="NodeReach"/>, because
    /// the two are not comparable: <see cref="NodeReach.WithLink"/> admits anonymous
    /// strangers holding an id and hides the node from every listing, while this admits
    /// nobody anonymous and lists the node to everybody else. Forced onto one ordered
    /// scale, inheritance — which is a maximum — would have to discard one of them, and an
    /// unlisted page inside an authenticated directory would silently vanish from the
    /// listings of the very people the directory was shared with.
    ///
    /// <see cref="AccessMode.Public"/> is deliberately not included: a public node already
    /// reaches everyone through <see cref="NodeReach.Listed"/>, and routing it through here
    /// as well would let it outlive <c>Sharing.AllowPublic</c>, whose documented meaning is
    /// that a node claiming to be public is treated as private by every query.</summary>
    public static bool ListsToSignedIn(this AccessMode mode) => mode == AccessMode.Authenticated;
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
