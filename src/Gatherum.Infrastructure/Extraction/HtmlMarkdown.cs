using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>Renders parsed HTML as Markdown — how a captured web page reads to a model.
/// The whole body is converted rather than a guessed-at "main content": deciding what
/// part of a page matters is the reader's judgment call, not an extractor's. Fidelity
/// is prose-first: headings, lists, links, tables, quotes and code survive; nothing is
/// escaped, because the output is for reading and searching, not for round-tripping.
/// Inlined images are the one deliberate loss — a bookmark snapshot carries them as
/// data: URIs megabytes long, and a wall of base64 is the opposite of what this
/// rendering is for, so they fall back to their alt text.</summary>
public static partial class HtmlMarkdown
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "svg", "iframe", "object", "embed",
        "head", "select", "option", "datalist",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "details", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4",
        "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section",
        "summary", "table", "ul",
    };

    public static string Render(IElement root)
    {
        var text = new StringBuilder();
        Flow(root, text);
        return TidyBlankLines().Replace(text.ToString(), "\n\n").Trim();
    }

    /// <summary>Mixed content, the way HTML actually arrives: inline runs accumulate
    /// into a paragraph until a block interrupts them.</summary>
    private static void Flow(IElement parent, StringBuilder text)
    {
        var run = new StringBuilder();
        foreach (var child in parent.ChildNodes)
        {
            if (child is IText words)
                run.Append(Spaced(words.Data));
            else if (child is IElement element && !Skipped.Contains(element.LocalName))
            {
                if (Blocks.Contains(element.LocalName))
                {
                    FlushRun(run, text);
                    Block(element, text);
                }
                else
                {
                    run.Append(Inline(element));
                }
            }
        }
        FlushRun(run, text);
    }

    private static void FlushRun(StringBuilder run, StringBuilder text)
    {
        var paragraph = CollapseLines(run.ToString());
        run.Clear();
        if (paragraph.Length > 0)
            text.Append(paragraph).Append("\n\n");
    }

    private static void Block(IElement element, StringBuilder text)
    {
        switch (element.LocalName)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var level = element.LocalName[1] - '0';
                var heading = CollapseLines(InlineChildren(element)).Replace('\n', ' ');
                if (heading.Length > 0)
                    text.Append(new string('#', level)).Append(' ').Append(heading)
                        .Append("\n\n");
                break;
            case "hr":
                text.Append("---\n\n");
                break;
            case "pre":
                Fenced(element, text);
                break;
            case "blockquote":
                Quoted(element, text);
                break;
            case "ul" or "ol":
                Listed(element, text);
                break;
            case "table":
                Tabled(element, text);
                break;
            default:
                // p and the other simple containers, and every division that merely
                // groups: their content flows, and nested blocks interrupt it there.
                Flow(element, text);
                break;
        }
    }

    private static void Fenced(IElement pre, StringBuilder text)
    {
        var code = pre.TextContent.TrimEnd();
        var language = pre.QuerySelector("code")?.ClassList
            .FirstOrDefault(c => c.StartsWith("language-", StringComparison.Ordinal))
            ?["language-".Length..] ?? "";
        var fence = code.Contains("```", StringComparison.Ordinal) ? "````" : "```";
        text.Append(fence).Append(language).Append('\n')
            .Append(code).Append('\n').Append(fence).Append("\n\n");
    }

    private static void Quoted(IElement quote, StringBuilder text)
    {
        var inner = new StringBuilder();
        Flow(quote, inner);
        foreach (var line in inner.ToString().TrimEnd().Split('\n'))
            text.Append(line.Length > 0 ? "> " : ">").Append(line).Append('\n');
        text.Append('\n');
    }

    /// <summary>Nesting works by composition: a list inside an item renders itself,
    /// and the item's continuation indent pushes the whole thing one level in.</summary>
    private static void Listed(IElement list, StringBuilder text)
    {
        var ordered = list.LocalName == "ol";
        var index = int.TryParse(list.GetAttribute("start"), out var start) ? start : 1;
        foreach (var item in list.Children.Where(c => c.LocalName == "li"))
        {
            var marker = ordered ? $"{index++}. " : "- ";
            var pad = new string(' ', marker.Length);
            var inner = new StringBuilder();
            Flow(item, inner);
            // Tight lists: the blank lines block flow inserts would read as loose
            // items, and one item's paragraph-then-sublist is still one item.
            var lines = inner.ToString().Split('\n')
                .Where(line => line.Length > 0).ToList();
            for (var i = 0; i < lines.Count; i++)
                text.Append(i == 0 ? marker : pad).Append(lines[i]).Append('\n');
        }
        text.Append('\n');
    }

    private static void Tabled(IElement table, StringBuilder text)
    {
        var header = true;
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.Children
                .Where(c => c.LocalName is "td" or "th")
                .Select(c => CollapseLines(InlineChildren(c)).Replace('\n', ' ')
                    .Replace("|", "\\|"))
                .ToList();
            if (cells.Count == 0)
                continue;
            text.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
            if (header)
            {
                text.Append("| ")
                    .Append(string.Join(" | ", cells.Select(_ => "---")))
                    .Append(" |\n");
                header = false;
            }
        }
        text.Append('\n');
    }

    private static string InlineChildren(INode parent)
    {
        var text = new StringBuilder();
        foreach (var child in parent.ChildNodes)
        {
            if (child is IText words)
                text.Append(Spaced(words.Data));
            else if (child is IElement element && !Skipped.Contains(element.LocalName))
                text.Append(Inline(element));
        }
        return text.ToString();
    }

    private static string Inline(IElement element) => element.LocalName switch
    {
        "br" => "\n",
        "strong" or "b" => Marked("**", InlineChildren(element)),
        "em" or "i" => Marked("*", InlineChildren(element)),
        "del" or "s" or "strike" => Marked("~~", InlineChildren(element)),
        "code" or "kbd" or "samp" => Code(element),
        "a" => Linked(element),
        "img" => Pictured(element),
        _ => InlineChildren(element),
    };

    /// <summary>Emphasis marks hug their words: <c>** hot **</c> is not emphasis, so
    /// the whitespace an author left inside the tag moves outside the marks.</summary>
    private static string Marked(string mark, string inner)
    {
        var core = inner.Trim();
        if (core.Length == 0)
            return inner;
        var leading = inner.Length - inner.TrimStart().Length;
        var trailing = inner.Length - inner.TrimEnd().Length;
        return $"{inner[..leading]}{mark}{core}{mark}{inner[(inner.Length - trailing)..]}";
    }

    private static string Code(IElement element)
    {
        var code = Spaced(element.TextContent).Trim();
        return code.Length == 0 ? "" : $"`{code}`";
    }

    private static string Linked(IElement anchor)
    {
        var label = InlineChildren(anchor).Trim();
        var href = anchor.GetAttribute("href")?.Trim() ?? "";
        if (href.Length == 0 || href.StartsWith('#')
            || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return label;
        return label.Length > 0 ? $"[{label}]({href})" : $"<{href}>";
    }

    /// <summary>A snapshot's images are data: URIs by design; here they reduce to their
    /// alt text, because that is the part a reader of the rendering can use.</summary>
    private static string Pictured(IElement image)
    {
        var alt = image.GetAttribute("alt")?.Trim() ?? "";
        var src = image.GetAttribute("src")?.Trim() ?? "";
        if (src.Length == 0 || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return alt.Length > 0 ? $"*[{alt}]*" : "";
        return $"![{alt}]({src})";
    }

    /// <summary>Markup whitespace means nothing beyond separation, so it becomes one
    /// space — line breaks included, which leaves '\n' meaning what a <c>br</c> or a
    /// block boundary said.</summary>
    private static string Spaced(string text) => MarkupWhitespace().Replace(text, " ");

    private static string CollapseLines(string text)
    {
        var lines = text.Split('\n')
            .Select(line => Spaces().Replace(line, " ").Trim());
        return string.Join('\n', lines).Trim('\n');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MarkupWhitespace();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex TidyBlankLines();
}
