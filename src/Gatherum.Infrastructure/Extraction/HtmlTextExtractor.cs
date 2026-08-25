using AngleSharp.Html.Parser;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>HTML — a bookmark's snapshot, or any page somebody saved — extracts as its
/// Markdown rendering, the same convention docx set: search still finds the words, and
/// an agent reading the node over MCP gets the page as structured prose rather than
/// markup. Registered ahead of <see cref="PlainTextExtractor"/>, which would otherwise
/// claim the file and index its tags.</summary>
public class HtmlTextExtractor : ITextExtractor
{
    private const int MaxChars = 4 * 1024 * 1024;

    public bool CanExtract(string mediaType, string fileName) =>
        mediaType == MediaTypes.Html
        || MediaTypes.Resolve(mediaType, fileName) == MediaTypes.Html;

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(content, cancellationToken);

        var markdown = document.Body is { } body ? HtmlMarkdown.Render(body) : "";
        // The title leads, as a heading — unless the page opens by saying it itself.
        if (document.Title?.Trim() is { Length: > 0 } title
            && !markdown.StartsWith($"# {title}\n", StringComparison.Ordinal)
            && markdown != $"# {title}")
            markdown = markdown.Length > 0 ? $"# {title}\n\n{markdown}" : $"# {title}";

        return markdown.Length <= MaxChars ? markdown : markdown[..MaxChars];
    }
}
