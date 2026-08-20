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

    /// <summary>Words the medium itself carries, read out of it by a model: text on a
    /// still image, speech in audio or video. Empty until analysis finishes — and
    /// legitimately empty afterwards for a photo of a landscape.</summary>
    public string Transcript { get; set; } = "";

    /// <summary>A model's description of the medium, so a recording or a photograph is
    /// findable by what it is about and not only by the words inside it.</summary>
    public string Summary { get; set; } = "";

    public MediaAnalysisState Analysis { get; set; }

    /// <summary>Why analysis failed, kept so the file view can say so rather than show
    /// an unexplained blank.</summary>
    public string AnalysisError { get; set; } = "";

    public Guid UploadedById { get; init; }
    public User? UploadedBy { get; init; }
    public DateTimeOffset UploadedAt { get; init; }
}

/// <summary>Where a version stands with <see cref="Abstractions.IMediaAnalyzer"/>.
/// <see cref="None"/> is the answer for everything that needs no model — every text
/// file, every PDF, and all media when no analyzer is configured — which is what keeps
/// an unconfigured Gatherum behaving exactly as it did before.</summary>
public enum MediaAnalysisState
{
    None,
    Pending,
    Complete,
    Failed,
}
