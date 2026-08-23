namespace Gatherum.Core.Domain;

public class User
{
    public Guid Id { get; init; }
    public required string Subject { get; init; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }

    /// <summary>The name the identity provider knows them by — <c>preferred_username</c>,
    /// which for Authelia is the login itself. This is what their directory is named
    /// after, so it is the one claim whose exact spelling reaches the filesystem.</summary>
    public required string Username { get; set; }
    public bool IsAdmin { get; set; }

    /// <summary>The directory this user owns, directly under the storage root. Ownership
    /// is the path, so this name is the whole of the mapping: everything found beneath it
    /// is theirs, and a reindex learns who owns what by reading the layout.
    ///
    /// Derived from <see cref="Username"/> at first sign-in and never changed after.
    /// Renaming it would mean moving every file the user owns, and a directory that
    /// quietly moved out from under somebody's rsync is worse than one whose name has
    /// aged.</summary>
    public required string RootName { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
