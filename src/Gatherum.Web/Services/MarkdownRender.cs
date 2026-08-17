using Gatherum.Core.Markdown;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Gatherum.Web.Services;

/// <summary>Renders Gatherum markdown to safe HTML: raw HTML is disabled, mentions
/// (node://id) become in-app links, and "> [!kind]" quotes render as callouts.</summary>
public static class MarkdownRender
{
    public static string ToHtml(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, MarkdownContent.Pipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.Url?.StartsWith("node://", StringComparison.Ordinal) == true)
            {
                link.Url = "/nodes/" + link.Url["node://".Length..];
                link.GetAttributes().AddClass("mention");
            }
        }

        foreach (var quote in document.Descendants<QuoteBlock>())
            PromoteCallout(quote);

        var writer = new StringWriter();
        var renderer = new Markdig.Renderers.HtmlRenderer(writer);
        MarkdownContent.Pipeline.Setup(renderer);
        renderer.Render(document);
        return writer.ToString();
    }

    private static void PromoteCallout(QuoteBlock quote)
    {
        // The marker may be split over several literals ("[" alone after a failed link
        // parse), so match against the concatenated leading literals.
        if (quote.FirstOrDefault() is not ParagraphBlock paragraph || paragraph.Inline is null)
            return;
        var literals = paragraph.Inline.TakeWhile(i => i is LiteralInline)
            .Cast<LiteralInline>().ToList();
        var leading = string.Concat(literals.Select(l => l.Content.ToString()));
        var match = System.Text.RegularExpressions.Regex.Match(leading, @"^\[!(\w+)\]\s*");
        if (!match.Success)
            return;

        var kind = match.Groups[1].Value.ToLowerInvariant();
        quote.GetAttributes().AddClass("callout");
        quote.GetAttributes().AddClass($"callout-{kind}");

        var remaining = match.Value.Length;
        foreach (var literal in literals)
        {
            var take = Math.Min(remaining, literal.Content.Length);
            literal.Content.Start += take;
            remaining -= take;
            if (literal.Content.IsEmpty)
                literal.Remove();
            if (remaining == 0)
                break;
        }
        if (paragraph.Inline.FirstChild is LineBreakInline lineBreak)
            lineBreak.Remove();
    }
}
