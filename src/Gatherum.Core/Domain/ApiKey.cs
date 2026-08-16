namespace Gatherum.Core.Domain;

/// <summary>A programmatic-access credential. Only the SHA-256 of the secret is stored;
/// the plaintext is shown once at creation.</summary>
public class ApiKey
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public User? User { get; init; }
    public required string Name { get; init; }
    public required string KeyHash { get; init; }
    public required string Prefix { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}
