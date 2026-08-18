using System.Text;
using Gatherum.Core.Abstractions;
using UglyToad.PdfPig;

namespace Gatherum.Infrastructure.Extraction;

public class PdfTextExtractor : ITextExtractor
{
    public bool CanExtract(string mediaType, string fileName) =>
        mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        // PdfPig needs a seekable stream; uploads arrive as forward-only streams.
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;

        using var document = PdfDocument.Open(buffered);
        var text = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.AppendLine(page.Text);
        }
        return text.ToString();
    }
}
