namespace Gatherum.Core.Domain;

public class User
{
    public Guid Id { get; init; }
    public required string Subject { get; init; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public bool IsAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
