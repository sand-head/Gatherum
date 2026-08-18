using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using SlopEdit.Docx;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>docx search text via slopedit's converter: the canonical Markdown
/// rendering, the same vocabulary the document editor round-trips — so what you can
/// see in the editor is what search can find.</summary>
public class DocxTextExtractor : ITextExtractor
{
    public bool CanExtract(string mediaType, string fileName) =>
        mediaType.Equals(MediaTypes.Docx, StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        // The converter needs a seekable stream; uploads arrive forward-only.
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;
        return DocxConverter.ToMarkdown(buffered);
    }
}
