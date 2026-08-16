using System.Text.Json.Nodes;
using Gatherum.Core.Markdown;

namespace Gatherum.Tests;

public class PageMarkdownTests
{
    [Fact]
    public void Markdown_survives_a_round_trip_through_the_editor_document()
    {
        var markdown =
            """
            # Field notes

            A paragraph with **bold**, *italic*, ~~struck~~, `code`, and a [link](https://example.org).

            ## Lists

            - first
            - second

            1. one
            2. two

            - [ ] open task
            - [x] done task

            > A plain quote.

            > [!warning]
            > Mind the gap.

            ```csharp
            var answer = 42;
            ```

            | Name | Role |
            | --- | --- |
            | Jess | admin |

            ---

            ![diagram](/api/files/1b8c9976-9f4d-4a6a-a8f5-64437292c1e7/content)
            """;

        var doc = PageMarkdown.ToDocJson(markdown);
        var roundTripped = PageMarkdown.ToMarkdown(doc);

        Assert.Equal(markdown.ReplaceLineEndings("\n"), roundTripped);
    }

    [Fact]
    public void Mentions_round_trip_and_register_as_links()
    {
        var id = Guid.NewGuid();
        var markdown = $"Ask [@Jess's notes](node://{id}) about it.";

        var doc = PageMarkdown.ToDocJson(markdown);

        var mention = FindNodes(JsonNode.Parse(doc)!, "mention").Single();
        Assert.Equal(id.ToString(), (string?)mention["attrs"]?["id"]);
        Assert.Equal("Jess's notes", (string?)mention["attrs"]?["label"]);
        Assert.Equal(markdown, PageMarkdown.ToMarkdown(doc));
        Assert.Contains(id, PageMarkdown.LinkedNodeIds(doc));
    }

    [Fact]
    public void Embedded_file_content_counts_as_a_link()
    {
        var id = Guid.NewGuid();
        var doc = PageMarkdown.ToDocJson($"![photo](/api/files/{id}/content)");

        Assert.Equal([id], PageMarkdown.LinkedNodeIds(doc));
    }

    [Fact]
    public void External_links_and_images_are_not_node_links()
    {
        var doc = PageMarkdown.ToDocJson("[site](https://example.org) ![pic](https://example.org/x.png)");

        Assert.Empty(PageMarkdown.LinkedNodeIds(doc));
    }

    [Fact]
    public void Plain_text_flattens_structure_but_keeps_words()
    {
        var doc = PageMarkdown.ToDocJson(
            """
            # Quadlets

            Rootless **Podman** units live in `~/.config/containers`.

            - [x] port the postgres unit
            """);

        var text = PageMarkdown.ToPlainText(doc);

        Assert.Contains("Quadlets", text);
        Assert.Contains("Rootless Podman units", text);
        Assert.Contains("port the postgres unit", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("#", text);
    }

    [Fact]
    public void Special_characters_are_escaped_and_unescaped()
    {
        var doc = PageMarkdown.ToDocJson(@"literal \*stars\* and \[brackets\]");

        var text = PageMarkdown.ToPlainText(doc);
        Assert.Equal("literal *stars* and [brackets]", text);
        Assert.Equal(text, PageMarkdown.ToPlainText(PageMarkdown.ToDocJson(PageMarkdown.ToMarkdown(doc))));
    }

    [Fact]
    public void Empty_markdown_becomes_the_empty_document()
    {
        var doc = PageMarkdown.ToDocJson("");

        Assert.Equal(PageMarkdown.EmptyDoc, doc);
        Assert.Equal("", PageMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void Callout_kinds_survive_the_round_trip()
    {
        var markdown = "> [!tip]\n> Backups are love letters to your future self.";

        var doc = PageMarkdown.ToDocJson(markdown);

        var callout = FindNodes(JsonNode.Parse(doc)!, "callout").Single();
        Assert.Equal("tip", (string?)callout["attrs"]?["kind"]);
        Assert.Equal(markdown, PageMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void Nested_lists_round_trip()
    {
        var markdown =
            """
            - outer
              - inner one
              - inner two
            - closing
            """;

        var doc = PageMarkdown.ToDocJson(markdown);

        Assert.Equal(markdown.ReplaceLineEndings("\n"), PageMarkdown.ToMarkdown(doc));
    }

    private static IEnumerable<JsonObject> FindNodes(JsonNode node, string type)
    {
        if (node is JsonObject obj)
        {
            if ((string?)obj["type"] == type)
                yield return obj;
            foreach (var child in obj.Select(p => p.Value).OfType<JsonNode>())
                foreach (var found in FindNodes(child, type))
                    yield return found;
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
                foreach (var found in FindNodes(child, type))
                    yield return found;
        }
    }
}
