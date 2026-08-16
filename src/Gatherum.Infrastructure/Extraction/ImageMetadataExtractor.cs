using System.Text;
using Gatherum.Core.Abstractions;
using MetadataExtractor;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>Makes photos findable by their EXIF and basic metadata: camera model,
/// capture date, GPS, dimensions.</summary>
public class ImageMetadataExtractor : ITextExtractor
{
    public bool CanExtract(string mediaType, string fileName) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
        !mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;

        var text = new StringBuilder();
        foreach (var directory in ImageMetadataReader.ReadMetadata(buffered))
        {
            foreach (var tag in directory.Tags)
            {
                if (tag.Description is { Length: > 0 } description)
                    text.AppendLine($"{tag.Name}: {description}");
            }
        }
        return text.ToString();
    }
}
