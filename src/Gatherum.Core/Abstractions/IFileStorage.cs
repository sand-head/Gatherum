namespace Gatherum.Core.Abstractions;

/// <summary>Content-addressed blob storage: bytes go in, their SHA-256 comes back.
/// Implementations must be safe to call with the same content twice.</summary>
public interface IFileStorage
{
    Task<StoredBlob> SaveAsync(Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string hash, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default);
}

public record StoredBlob(string Hash, long SizeBytes);
