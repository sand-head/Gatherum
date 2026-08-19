using Gatherum.Client;

namespace Gatherum.Tests;

/// <summary>The red link: which titles a document asks for, and what it looks like
/// when nobody has written one of them yet.</summary>
public class WikiLinkInkTests
{
    private static readonly ChromeInk Ink = ChromeInk.For(isDark: false);

    [Fact]
    public void A_document_names_the_titles_it_links_once_each()
    {
        var doc = GatherumMarkdown.Parse(
            "[[Homelab]] and [[homelab]] and [[Quadlet notes|the notes]].", isDark: false);

        Assert.Equal(["Homelab", "Quadlet notes"], WikiLinks.TargetsIn(doc));
    }

    [Fact]
    public void Only_the_unresolved_link_goes_red()
    {
        var doc = GatherumMarkdown.Parse("[[Homelab]] and [[Nowhere]].", isDark: false);
        var live = new HashSet<string>(["Homelab"], StringComparer.OrdinalIgnoreCase);

        Assert.True(WikiLinks.Mark(doc, live, Ink));

        var runs = doc.Blocks[0].Runs.Where(r => r.Style.Link is not null).ToList();
        Assert.Equal(default, runs[0].Style.Color);      // live: the theme's link color
        Assert.Equal(Ink.DeadLink, runs[1].Style.Color);
    }

    [Fact]
    public void Inking_twice_changes_nothing_the_second_time()
    {
        var doc = GatherumMarkdown.Parse("[[Nowhere]].", isDark: false);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(WikiLinks.Mark(doc, live, Ink));
        Assert.False(WikiLinks.Mark(doc, live, Ink));
    }

    [Fact]
    public void A_link_that_finds_its_page_loses_the_red()
    {
        var doc = GatherumMarkdown.Parse("[[Homelab]].", isDark: false);
        WikiLinks.Mark(doc, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Ink);

        Assert.True(WikiLinks.Mark(doc,
            new HashSet<string>(["Homelab"], StringComparer.OrdinalIgnoreCase), Ink));
        Assert.Equal(default, doc.Blocks[0].Runs[0].Style.Color);
    }

    [Theory]
    [InlineData("node://0f8f6e1a-0000-0000-0000-000000000001", true)]
    [InlineData("/api/files/0f8f6e1a-0000-0000-0000-000000000001/content", true)]
    [InlineData("/api/files/not-a-guid/content", false)]
    [InlineData("https://example.org", false)]
    public void In_app_urls_are_the_two_shapes_that_name_a_node(string url, bool isNode)
    {
        Assert.Equal(isNode, NodeUrl.TryParse(url, out _));
    }

    [Theory]
    [InlineData("https://example.org", true)]
    [InlineData("mailto:someone@example.org", true)]
    [InlineData("node://0f8f6e1a-0000-0000-0000-000000000001", false)]
    [InlineData("/nodes/x", false)]
    public void Only_a_scheme_the_browser_owns_leaves_the_app(string url, bool external)
    {
        Assert.Equal(external, NodeUrl.IsExternal(url));
    }
}
