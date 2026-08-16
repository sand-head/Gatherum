namespace Gatherum.Core.Abstractions;

/// <summary>Pulls searchable text out of an uploaded file. Register an implementation
/// in DI to support a new format; the first extractor that claims a file wins.</summary>
public interface ITextExtractor
{
    bool CanExtract(string mediaType, string fileName);
    Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default);
}
