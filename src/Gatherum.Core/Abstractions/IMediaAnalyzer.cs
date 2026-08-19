namespace Gatherum.Core.Abstractions;

/// <summary>Reads meaning out of a medium that carries no text of its own: words
/// photographed on a whiteboard, speech in a recording, what a video is about.
/// Deliberately not an <see cref="ITextExtractor"/> — that seam is exact, cheap, and
/// runs inside the upload request, while an analyzer asks a model, takes seconds to
/// minutes, and runs on a background worker once the bytes are already safe.</summary>
public interface IMediaAnalyzer
{
    bool CanAnalyze(string mediaType, string fileName);
    Task<MediaAnalysis> AnalyzeAsync(MediaSource source, CancellationToken cancellationToken = default);
}

/// <summary>What a model made of a medium. <paramref name="Transcript"/> is the words
/// the medium itself carries — read off a still image, spoken aloud in audio or video.
/// <paramref name="Summary"/> describes it, so a photo answers to what it is *of* and
/// not only to the words that happen to appear in it.</summary>
public record MediaAnalysis(string Transcript, string Summary)
{
    public static readonly MediaAnalysis Empty = new("", "");

    public bool IsEmpty => Transcript.Length == 0 && Summary.Length == 0;
}

/// <summary>The blob an analyzer works on. <paramref name="OpenAsync"/> hands back a
/// fresh stream each call because taking a video apart reads the bytes more than once —
/// the audio track, then the frames.</summary>
public record MediaSource(
    string Hash,
    string MediaType,
    string FileName,
    long SizeBytes,
    Func<CancellationToken, Task<Stream>> OpenAsync);
