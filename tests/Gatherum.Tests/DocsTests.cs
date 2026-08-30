using System.Text.RegularExpressions;
using Gatherum.Client;
using Gatherum.Web.Services;

namespace Gatherum.Tests;

/// <summary>
/// The manual that ships inside the app. Documentation rots quietly, so what can be
/// checked is: that every page is there and has something to say, that no link inside it
/// points at a page that does not exist, and that the page about the Markdown dialect
/// still names every construct the code actually implements.
/// </summary>
public class DocsTests
{
    private static readonly DocsLibrary Docs = new();

    private static DocPage Page(string slug) =>
        Docs.Find(slug) ?? throw new Xunit.Sdk.XunitException($"No documentation page '{slug}'.");

    [Fact]
    public void Every_page_has_a_title_and_something_to_say()
    {
        Assert.NotEmpty(Docs.Pages);
        Assert.All(Docs.Pages, page =>
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Title), $"{page.Slug} has no title.");
            Assert.False(string.IsNullOrWhiteSpace(page.Summary), $"{page.Slug} has no summary.");
            Assert.Contains("<h1", page.Html, StringComparison.Ordinal);
        });
        Assert.Equal(Docs.Pages.Select(p => p.Slug).Distinct().Count(), Docs.Pages.Count);
        Assert.Equal("index", Docs.Home.Slug);
        Assert.Same(Docs.Home, Docs.Pages[0]);
    }

    [Fact]
    public void A_page_is_found_however_its_name_is_typed_and_only_when_it_exists()
    {
        Assert.Equal("markdown", Page("MarkDown").Slug);
        Assert.Same(Docs.Home, Docs.Find(null));
        Assert.Null(Docs.Find("no-such-page"));
    }

    [Fact]
    public void Every_link_the_manual_makes_to_itself_lands_somewhere()
    {
        // The two assembled documents are not pages, so they are named rather than looked up.
        var served = Docs.Pages.Select(p => p.Slug)
            .Concat(["all.md", "llms.txt"]).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var page in Docs.Pages)
        {
            foreach (Match link in Regex.Matches(page.Markdown, @"\]\(/docs/([^)]*)\)"))
            {
                var target = link.Groups[1].Value.Split('#')[0];
                if (target.EndsWith(".md", StringComparison.Ordinal) && target != "all.md")
                    target = target[..^".md".Length];
                Assert.True(target.Length == 0 || served.Contains(target),
                    $"{page.Slug}.md links /docs/{link.Groups[1].Value}, which nothing serves.");
            }
        }
    }

    [Fact]
    public void The_dialect_page_names_every_construct_the_code_implements()
    {
        // The point of shipping the manual is that a model can be pointed at it, so a
        // construct the editor understands and this page never mentions is a bug in the
        // documentation rather than a gap in it.
        var dialect = Page("markdown").Markdown;

        Assert.Contains($":::{BlockTags.Infobox}", dialect, StringComparison.Ordinal);
        Assert.Contains($":::{BlockTags.Figure}", dialect, StringComparison.Ordinal);
        Assert.Contains($":::{BlockTags.Collection}", dialect, StringComparison.Ordinal);
        Assert.Contains("[[Title]]", dialect, StringComparison.Ordinal);
        Assert.Contains("node://", dialect, StringComparison.Ordinal);
        Assert.Contains("/api/files/", dialect, StringComparison.Ordinal);
        Assert.All(CalloutExtension.Kinds.Keys, kind =>
            Assert.Contains($"[!{kind.ToUpperInvariant()}]", dialect, StringComparison.Ordinal));
        // The container's own additions in slopedit 2.5, which the page owes a reader
        // just the same: footnotes and Pandoc's scripts.
        Assert.Contains("[^key]", dialect, StringComparison.Ordinal);
        Assert.Contains("x^2^", dialect, StringComparison.Ordinal);
        Assert.Contains("H~2~O", dialect, StringComparison.Ordinal);
        // And the 2.5.11 additions: image captions with their attributes, and the
        // delimiter-row alignments the tables finally keep.
        Assert.Contains("{width=", dialect, StringComparison.Ordinal);
        Assert.Contains("align=center", dialect, StringComparison.Ordinal);
        Assert.Contains(":---:", dialect, StringComparison.Ordinal);
    }

    [Fact]
    public void The_whole_manual_is_every_page_with_links_something_else_can_follow()
    {
        var manual = Docs.Manual("https://wiki.example.org");

        Assert.All(Docs.Pages, page => Assert.Contains($"# {page.Title}", manual, StringComparison.Ordinal));
        // A page read a long way from the app it came from cannot follow a relative link.
        Assert.Contains("](https://wiki.example.org/docs/markdown)", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("](/docs/", manual, StringComparison.Ordinal);
    }

    [Fact]
    public void The_llms_index_points_at_every_page_and_at_the_whole_thing()
    {
        var index = Docs.LlmsTxt("https://wiki.example.org");

        Assert.StartsWith("# Gatherum", index, StringComparison.Ordinal);
        Assert.Contains("https://wiki.example.org/docs/all.md", index, StringComparison.Ordinal);
        Assert.All(Docs.Pages, page => Assert.Contains(
            $"[{page.Title}](https://wiki.example.org/docs/{page.Slug}.md)", index, StringComparison.Ordinal));
    }
}
