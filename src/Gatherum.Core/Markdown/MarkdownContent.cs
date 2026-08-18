using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Gatherum.Core.Markdown;

/// <summary>Reads Gatherum's linking conventions out of Markdown text:
/// mentions are <c>[@Title](node://id)</c>, embedded files are
/// <c>![alt](/api/files/id/content)</c>.</summary>
public static class MarkdownContent
{
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .DisableHtml()
        .Build();

    public static IReadOnlySet<Guid> LinkedNodeIds(string markdown)
    {
        var ids = new HashSet<Guid>();
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (NodeIdFromUrl(link.Url) is { } id)
                ids.Add(id);
        }
        return ids;
    }

    public static Guid? NodeIdFromUrl(string? url)
    {
        if (url is null)
            return null;
        if (url.StartsWith("node://", StringComparison.Ordinal) &&
            Guid.TryParse(url["node://".Length..], out var mentionId))
            return mentionId;
        var match = System.Text.RegularExpressions.Regex.Match(
            url, @"^/api/files/([0-9a-fA-F-]{36})/content");
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var fileId) ? fileId : null;
    }

    /// <summary>Node ids mentioned as node://… anywhere in free text (file descriptions).</summary>
    public static IReadOnlySet<Guid> MentionedNodeIds(string text)
    {
        var ids = new HashSet<Guid>();
        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(text, @"node://([0-9a-fA-F-]{36})"))
        {
            if (Guid.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }
}
