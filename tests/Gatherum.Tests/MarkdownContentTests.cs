using Gatherum.Core.Markdown;
using Gatherum.Web.Services;

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

    [Fact]
    public void Rendering_turns_mentions_into_app_links()
    {
        var id = Guid.NewGuid();
        var html = MarkdownRender.ToHtml($"Ask [@Jess's notes](node://{id}).");

        Assert.Contains($"href=\"/nodes/{id}\"", html);
        Assert.Contains("class=\"mention\"", html);
        Assert.Contains("@Jess's notes", html);
    }

    [Fact]
    public void Rendering_promotes_marked_quotes_to_callouts()
    {
        var html = MarkdownRender.ToHtml("> [!tip]\n> Backups are love letters.");

        Assert.Contains("callout callout-tip", html);
        Assert.Contains("Backups are love letters.", html);
        Assert.DoesNotContain("[!tip]", html);
    }

    [Fact]
    public void Rendering_disables_raw_html()
    {
        var html = MarkdownRender.ToHtml("<script>alert(1)</script> *fine*");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("<em>fine</em>", html);
    }

    [Fact]
    public void Tables_task_lists_and_strikethrough_render()
    {
        var html = MarkdownRender.ToHtml(
            """
            | a | b |
            | --- | --- |
            | 1 | 2 |

            - [x] done
            - [ ] open

            ~~gone~~
            """);

        Assert.Contains("<table>", html);
        Assert.Contains("checked", html);
        Assert.Contains("<del>gone</del>", html);
    }
}
