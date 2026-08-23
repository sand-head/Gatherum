namespace Gatherum.Core.Domain;

public class User
{
    public Guid Id { get; init; }
    public required string Subject { get; init; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public bool IsAdmin { get; set; }

    /// <summary>The directory this user owns, directly under the storage root. Ownership
    /// is the path, so this name is the whole of the mapping: everything found beneath it
    /// is theirs, and a reindex learns who owns what by reading the layout.</summary>
    public required string RootName { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
