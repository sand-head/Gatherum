using System.Text;
using AngleSharp.Html.Parser;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using Gatherum.Infrastructure.Epub;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>EPUB search text as the book's Markdown rendering: chapters in spine
/// order — the order the book reads in, not the order the zip happens to store
/// them — each converted the way <see cref="HtmlTextExtractor"/> converts a page,
/// with the title from the package metadata leading as a heading. How the book is
/// opened — and what a broken package falls back to — is <see cref="EpubBook"/>'s
/// business, shared with the reader.</summary>
public class EpubTextExtractor : ITextExtractor
{
    private const int MaxChars = 4 * 1024 * 1024;

    public bool CanExtract(string mediaType, string fileName) =>
        mediaType.Equals(MediaTypes.Epub, StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(fileName).Equals(".epub", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        using var book = await EpubBook.OpenAsync(content, cancellationToken);

        var parser = new HtmlParser();
        var text = new StringBuilder();
        if (book.Title?.Trim() is { Length: > 0 } heading)
            text.Append("# ").Append(heading);

        for (var index = 0; index < book.Chapters.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var chapter = book.OpenChapter(index);
            var document = await parser.ParseDocumentAsync(chapter, cancellationToken);
            if (document.Body is not { } body || HtmlMarkdown.Render(body) is not { Length: > 0 } markdown)
                continue;
            if (text.Length > 0)
                text.Append("\n\n");
            text.Append(markdown);
            if (text.Length > MaxChars)
                break;
        }

        var result = text.ToString();
        return result.Length <= MaxChars ? result : result[..MaxChars];
    }
}
