using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>HTML — a bookmark's snapshot, or any page somebody saved — is searchable by
/// what it says, not by its markup. Registered ahead of <see cref="PlainTextExtractor"/>,
/// which would otherwise claim the file and index its tags.</summary>
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
        foreach (var element in document.QuerySelectorAll("script, style, noscript").ToList())
            element.Remove();

        var text = new StringBuilder();
        if (document.Title?.Trim() is { Length: > 0 } title)
            text.Append(title).Append('\n');
        var body = new StringBuilder();
        if (document.Body is { } root)
            AppendText(root, body);
        text.Append(Collapse(body.ToString()));
        return text.Length <= MaxChars ? text.ToString() : text.ToString(0, MaxChars);
    }

    /// <summary>TextContent glues a minified page into one word soup — <c>&lt;h1&gt;A
    /// heading&lt;/h1&gt;&lt;p&gt;prose</c> carries no whitespace at all — so blocks get
    /// the line breaks their rendering implies, and table cells a space.</summary>
    private static void AppendText(IElement element, StringBuilder text)
    {
        foreach (var child in element.ChildNodes)
        {
            if (child is IText run)
                text.Append(run.Data);
            else if (child is IElement nested)
            {
                AppendText(nested, text);
                if (nested.LocalName is "td" or "th")
                    text.Append(' ');
                else if (nested.LocalName is "br" or "p" or "div" or "li" or "tr" or "dt"
                    or "dd" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote"
                    or "pre" or "section" or "article" or "header" or "footer" or "aside"
                    or "figure" or "figcaption" or "table" or "ul" or "ol")
                    text.Append('\n');
            }
        }
    }

    /// <summary>Markup's indentation is noise by the paragraph: runs of whitespace fold
    /// to one space, runs of blank lines to one newline.</summary>
    private static string Collapse(string text)
    {
        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        var pendingBreak = false;
        foreach (var c in text)
        {
            if (c == '\n' || c == '\r')
                pendingBreak = true;
            else if (char.IsWhiteSpace(c))
                pendingSpace = true;
            else
            {
                if (pendingBreak && result.Length > 0)
                    result.Append('\n');
                else if (pendingSpace && result.Length > 0)
                    result.Append(' ');
                pendingBreak = false;
                pendingSpace = false;
                result.Append(c);
            }
        }
        return result.ToString();
    }
}
