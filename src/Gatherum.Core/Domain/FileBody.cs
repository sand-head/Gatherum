namespace Gatherum.Core.Domain;

public class FileBody
{
    public Guid NodeId { get; init; }
    public Node? Node { get; init; }
    public string Description { get; set; } = "";
    public List<FileVersion> Versions { get; init; } = [];

    public FileVersion Current => Versions.MaxBy(v => v.Number)
        ?? throw new InvalidOperationException($"File node {NodeId} has no versions.");
}

/// <summary>One uploaded revision of a file node. Bytes live in content-addressed
/// storage under <see cref="Hash"/>; re-uploading appends a new version.</summary>
public class FileVersion
{
    public Guid Id { get; init; }
    public Guid NodeId { get; init; }
    public int Number { get; init; }
    public required string Hash { get; set; }
    public required string MediaType { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; set; }
    public string ExtractedText { get; set; } = "";
    public Guid UploadedById { get; init; }
    public User? UploadedBy { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
}
