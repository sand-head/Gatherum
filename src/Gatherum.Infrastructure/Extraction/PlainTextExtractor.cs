using System.Text;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>Text, markdown, code, and config files are their own search text.</summary>
public class PlainTextExtractor : ITextExtractor
{
    private const int MaxChars = 4 * 1024 * 1024;

    public bool CanExtract(string mediaType, string fileName) =>
        MediaTypes.IsText(mediaType, fileName);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        return new string(buffer, 0, read);
    }
}
