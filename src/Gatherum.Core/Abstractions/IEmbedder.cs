namespace Gatherum.Core.Abstractions;

/// <summary>Turns text into a point in a vector space where nearness is likeness, which
/// is what lets a search for "the noisy fan in the server closet" find a page that never
/// says any of those words. Its own seam rather than a corner of
/// <see cref="IMediaAnalyzer"/>: an analyzer reads one medium's bytes once and is done
/// with them, while this is asked the same question on every search and re-asked of
/// every node whose text changes. A remote runner and an in-process ONNX model are both
/// drop-ins behind it, and neither should mean touching a service.</summary>
public interface IEmbedder
{
    /// <summary>Name of the model behind this embedder. Stored beside every vector,
    /// because vectors from two models are not comparable and a swap has to invalidate
    /// rather than silently mis-rank.</summary>
    string Model { get; }

    /// <summary>Embeds a batch in one call — a local runner spends most of a request on
    /// overhead, so forty chunks at once is not forty times the work of one.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
