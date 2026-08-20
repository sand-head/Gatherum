using Gatherum.Core.Abstractions;

namespace Gatherum.Tests;

/// <summary>An analyzer with no model behind it. It claims the same media the real one
/// does, and answers with whatever a test told it to say.</summary>
public sealed class FakeMediaAnalyzer : IMediaAnalyzer
{
    public MediaAnalysis Answer { get; set; } = new("transcribed words", "a summary");
    public Exception? Throws { get; set; }
    public List<MediaSource> Analyzed { get; } = [];

    /// <summary>Off by default so the tests that predate analysis see the world they
    /// were written against: nothing claims an image, nothing queues.</summary>
    public bool Enabled { get; set; }

    public bool CanAnalyze(string mediaType, string fileName) =>
        Enabled &&
        (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
         mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
         mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) &&
        !mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    public Task<MediaAnalysis> AnalyzeAsync(MediaSource source,
        CancellationToken cancellationToken = default)
    {
        Analyzed.Add(source);
        return Throws is null ? Task.FromResult(Answer) : Task.FromException<MediaAnalysis>(Throws);
    }
}
