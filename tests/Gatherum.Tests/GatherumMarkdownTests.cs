using Gatherum.Client;
using SlopEdit.Core.Rich;

namespace Gatherum.Tests;

/// <summary>The dialect Gatherum teaches slopedit per call — wiki links, asides,
/// callouts — read, written, and dressed. Pure model work: no canvas, no server.</summary>
public class GatherumMarkdownTests
{
    private static RichDocument Parse(string markdown) =>
        GatherumMarkdown.Parse(markdown, isDark: false);

    [Fact]
    public void An_infobox_reads_as_tagged_blocks_and_writes_its_fence_back()
    {
        var doc = Parse("""
            Intro paragraph.

            :::infobox
            # Chapter 12
            | Words | 4,210 |
            :::
            """);

        var tagged = doc.Blocks.Where(b => b.Tag is not null).ToList();
        Assert.Equal(2, tagged.Count);
        Assert.All(tagged, b => Assert.Equal(BlockTags.Infobox, BlockTags.KindOf(b.Tag)));
        Assert.Equal(BlockKind.Heading, tagged[0].Kind);
        Assert.Equal(BlockKind.TableRow, tagged[1].Kind);
        Assert.False(tagged[1].TableGrid);          // an infobox is a table without a grid

        var written = GatherumMarkdown.ToMarkdown(doc);
        Assert.Contains(":::infobox", written);
        Assert.Contains("# Chapter 12", written);
        Assert.EndsWith(":::", written);
        // The writer adds the delimiter row Markdown tables want; from there the trip
        // is byte-stable.
        Assert.Equal(written, GatherumMarkdown.ToMarkdown(Parse(written)));
    }

    [Fact]
    public void Reading_an_infobox_and_writing_it_back_leaves_the_file_alone()
    {
        // Everything the construct is dressed with — centered headings, the missing
        // grid — is style Markdown cannot say, so merely opening a page never rewrites
        // it. (The delimiter row is the writer's, which is why the source has one.)
        var source = """
            :::infobox
            # Homelab
            | Location | The closet |
            | --- | --- |
            :::
            """;

        Assert.Equal(source, GatherumMarkdown.ToMarkdown(Parse(source)));
    }

    [Fact]
    public void A_figure_keeps_the_side_and_width_it_was_written_with()
    {
        var source = """
            :::figure left 360
            ![The rack](/api/files/0f8f6e1a-0000-0000-0000-000000000001/content)
            The homelab, before the rewire
            :::
            """;

        var doc = Parse(source);

        Assert.All(doc.Blocks.Where(b => b.Tag is not null),
            b => Assert.Equal("figure left 360", BlockTags.SourceOf(b.Tag)));
        Assert.Equal(source, GatherumMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void An_unterminated_fence_is_just_text()
    {
        var doc = Parse("""
            :::infobox
            # Never closed
            """);

        Assert.All(doc.Blocks, b => Assert.Null(b.Tag));
        Assert.Equal(":::infobox", doc.Blocks[0].Text);
    }

    [Fact]
    public void A_callout_carries_its_kind_and_its_title()
    {
        var source = """
            > [!WARNING] Restores are not backups
            > Old bytes survive; a deleted node's do not.
            """;

        var doc = Parse(source);

        Assert.Equal(2, doc.Blocks.Count);
        Assert.All(doc.Blocks, b => Assert.Equal(BlockKind.Quote, b.Kind));
        Assert.All(doc.Blocks, b => Assert.Equal("callout warning", BlockTags.SourceOf(b.Tag)));
        Assert.Equal("Restores are not backups", doc.Blocks[0].Text);
        Assert.True(doc.Blocks[0].Runs[0].Style.Has(InlineFlags.Bold));
        Assert.Equal(source, GatherumMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void A_bare_marker_wears_its_kind_as_a_title_and_stays_bare()
    {
        var doc = Parse("> [!NOTE]\n> Worth knowing.");

        Assert.Equal("Note", doc.Blocks[0].Text);
        Assert.Equal("> [!NOTE]\n> Worth knowing.", GatherumMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void Two_callouts_in_a_row_stay_two_callouts()
    {
        var source = """
            > [!NOTE]
            > First.
            > [!TIP]
            > Second.
            """;

        var doc = Parse(source);

        Assert.Equal(["callout note", "callout note", "callout tip", "callout tip"],
            doc.Blocks.Select(b => BlockTags.SourceOf(b.Tag)));
        // Same words, different constructs: the tags must not be equal, or the writer
        // would take both runs for one callout.
        Assert.NotEqual(doc.Blocks[0].Tag, doc.Blocks[2].Tag);
        Assert.Equal(source, GatherumMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void A_quote_that_only_looks_like_a_callout_stays_a_quote()
    {
        var doc = Parse("> [!nope] not one of the five");

        Assert.All(doc.Blocks, b => Assert.Null(b.Tag));
        Assert.Equal(BlockKind.Quote, doc.Blocks[0].Kind);
        // The brackets come back escaped, as any literal bracket in prose does; what
        // matters is that the text is unchanged and the trip is stable.
        Assert.Equal("[!nope] not one of the five", doc.Blocks[0].Text);
        var written = GatherumMarkdown.ToMarkdown(doc);
        Assert.Equal(written, GatherumMarkdown.ToMarkdown(Parse(written)));
    }

    [Fact]
    public void Two_of_the_same_construct_in_a_row_stay_two()
    {
        var source = """
            > [!NOTE] First
            > One.
            > [!NOTE] Second
            > Two.

            :::infobox
            # One
            :::

            :::infobox
            # Two
            :::
            """;

        var doc = Parse(source);

        Assert.Equal(4, doc.Blocks.Count(b => b.Tag is not null && BlockTags.IsCallout(b.Tag)));
        Assert.Equal(2, doc.Floats.Count);
        Assert.Equal(source, GatherumMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void A_link_in_a_callouts_title_survives_the_trip()
    {
        var source = "> [!NOTE] See [[Homelab]] first\n> The rest is prose.";

        var doc = Parse(source);

        Assert.Contains(doc.Blocks[0].Runs,
            r => r.Style.Link == WikiLinkExtension.UrlOf("Homelab"));
        Assert.Equal(source, GatherumMarkdown.ToMarkdown(doc));
    }

    [Theory]
    [InlineData("Linked from [[Homelab]] here.", "Homelab", "Homelab")]
    [InlineData("Linked from [[Homelab\\|the rack]] here.", "Homelab", "the rack")]
    public void A_wiki_link_reads_as_a_link_run_and_writes_its_own_spelling(
        string source, string target, string label)
    {
        var doc = Parse(source);

        var link = doc.Blocks[0].Runs.Single(r => r.Style.Link is not null);
        Assert.Equal(WikiLinkExtension.UrlOf(target), link.Style.Link);
        Assert.Equal(label, link.Text);
        Assert.Equal(source, GatherumMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void Constructs_compose_a_wiki_link_inside_an_infobox_cell()
    {
        var source = """
            :::infobox
            | Site | [[Homelab]] |
            :::
            """;

        var doc = Parse(source);

        var row = doc.Blocks.Single(b => b.Kind == BlockKind.TableRow);
        Assert.Equal(BlockTags.Infobox, BlockTags.KindOf(row.Tag));
        Assert.Contains(row.Runs, r => r.Style.Link == WikiLinkExtension.UrlOf("Homelab"));
        Assert.Contains("[[Homelab]]", GatherumMarkdown.ToMarkdown(doc));
    }

    [Fact]
    public void An_aside_floats_and_wears_a_card()
    {
        var doc = Parse("""
            Intro paragraph.

            :::infobox
            # Chapter 12
            | Words | 4,210 |
            :::
            """);

        var floated = Assert.Single(doc.Floats);
        Assert.Equal(FloatSide.Right, floated.Side);
        Assert.Equal(2, floated.BlockCount);
        Assert.Equal(doc.Blocks.Count - 2, floated.FirstBlock);
        // The card, then the band behind the heading.
        Assert.Equal(2, doc.Decorations.Count);
        Assert.Equal(ChromeInk.For(false).CardFill, doc.Decorations[0].Background);
        Assert.Equal(1, doc.Decorations[1].BlockCount);
    }

    [Fact]
    public void A_figures_own_side_and_width_reach_the_layout()
    {
        var doc = Parse("""
            :::figure left 360
            ![The rack](/api/files/0f8f6e1a-0000-0000-0000-000000000001/content)
            :::
            """);

        var floated = Assert.Single(doc.Floats);
        Assert.Equal(FloatSide.Left, floated.Side);
        Assert.Equal(360f, floated.WidthPx);
    }

    [Fact]
    public void Chrome_follows_the_blocks_when_they_move()
    {
        var doc = Parse("""
            Intro paragraph.

            :::infobox
            # Chapter 12
            :::
            """);
        Assert.Equal(2, doc.Floats[0].FirstBlock);

        // The caret starts at the top of the document, outside the aside.
        doc.Insert("One more line above it.\n");
        DocumentChrome.Apply(doc, isDark: false);

        Assert.Equal(3, doc.Floats[0].FirstBlock);
        Assert.Equal(3, doc.Decorations[0].FirstBlock);
    }

    [Fact]
    public void A_callout_is_painted_but_not_floated()
    {
        var doc = Parse("> [!CAUTION] Careful\n> Here be dragons.");

        Assert.Empty(doc.Floats);
        var box = Assert.Single(doc.Decorations);
        Assert.Equal(2, box.BlockCount);

        var (fill, border, ink) = ChromeInk.For(false).Callout("caution");
        Assert.Equal(fill, box.Background);
        Assert.Equal(border, box.Border);
        Assert.Equal(ink, doc.Blocks[0].Runs[0].Style.Color);
    }

    [Fact]
    public void The_mode_decides_the_paint()
    {
        var light = GatherumMarkdown.Parse("> [!NOTE]\n> Hello.", isDark: false);
        var dark = GatherumMarkdown.Parse("> [!NOTE]\n> Hello.", isDark: true);

        Assert.NotEqual(light.Decorations[0].Background, dark.Decorations[0].Background);

        GatherumMarkdown.Dress(light, isDark: true);
        Assert.Equal(dark.Decorations[0].Background, light.Decorations[0].Background);
    }
}
