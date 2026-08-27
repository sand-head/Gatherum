using Gatherum.Core.Markdown;

namespace Gatherum.Tests;

public class MarkdownContentTests
{
    [Fact]
    public void Mentions_and_embedded_files_register_as_links()
    {
        var mention = Guid.NewGuid();
        var embed = Guid.NewGuid();
        var markdown =
            $"""
            See [@Quadlet notes](node://{mention}) and the diagram:

            ![diagram](/api/files/{embed}/content)
            """;

        var ids = MarkdownContent.LinkedNodeIds(markdown);

        Assert.Equal(2, ids.Count);
        Assert.Contains(mention, ids);
        Assert.Contains(embed, ids);
    }

    [Fact]
    public void External_links_and_images_are_not_node_links()
    {
        var ids = MarkdownContent.LinkedNodeIds(
            "[site](https://example.org) ![pic](https://example.org/x.png)");

        Assert.Empty(ids);
    }

    [Fact]
    public void A_citation_footnote_backlinks_the_node_it_cites()
    {
        // A citation is a footnote whose note is a mention — plain Markdown, so the
        // server's link pass sees it without knowing footnotes exist.
        var cited = Guid.NewGuid();
        var markdown =
            $"""
            The page moved on.[^1]

            [^1]: [Example Domain](node://{cited}), captured 27 August 2026 — [example.com](https://example.com/page).
            """;

        Assert.Equal([cited], MarkdownContent.LinkedNodeIds(markdown));
    }

    [Fact]
    public void Descriptions_link_by_bare_node_urls()
    {
        var id = Guid.NewGuid();

        Assert.Equal([id], MarkdownContent.MentionedNodeIds($"companion to node://{id} above"));
        Assert.Empty(MarkdownContent.MentionedNodeIds("no links here"));
    }
}
