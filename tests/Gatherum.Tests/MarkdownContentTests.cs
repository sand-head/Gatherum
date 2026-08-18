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
    public void Descriptions_link_by_bare_node_urls()
    {
        var id = Guid.NewGuid();

        Assert.Equal([id], MarkdownContent.MentionedNodeIds($"companion to node://{id} above"));
        Assert.Empty(MarkdownContent.MentionedNodeIds("no links here"));
    }
}
