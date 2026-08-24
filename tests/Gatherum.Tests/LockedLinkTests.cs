using Gatherum.Client;
using SlopEdit.Core.Rich;

namespace Gatherum.Tests;

/// <summary>The locked link: what a page looks like when it names a node its reader is
/// not allowed to open. A public page linking its author's private file is the ordinary
/// case, so the link has to say so rather than lead into a 404.</summary>
public class LockedLinkTests
{
    private static readonly ChromeInk Ink = ChromeInk.For(isDark: false);
    private static readonly Guid Open = Guid.Parse("8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f");
    private static readonly Guid Shut = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static RichDocument Page() => GatherumMarkdown.Parse($"""
        A [@Diary](node://{Shut}) beside a [@Notebook](node://{Open}).

        ![The rack](/api/files/{Shut}/content)
        """, isDark: false);

    private static IReadOnlySet<Guid> Reachable(params Guid[] ids) => ids.ToHashSet();

    private static Block Prose(RichDocument document) =>
        document.Blocks.First(b => b.Runs.Any(r => r.Text == "@Diary"));

    private static Block Picture(RichDocument document, int at) => document.Blocks[at];

    private static int PictureAt(RichDocument document) =>
        document.Blocks.ToList().FindIndex(b => b.Kind == BlockKind.Image);

    [Fact]
    public void A_document_names_every_node_it_links_or_embeds_once_each()
    {
        Assert.Equal([Shut, Open], NodeLinks.TargetsIn(Page()));
    }

    [Fact]
    public void Only_the_unreachable_link_is_locked()
    {
        var doc = Page();

        Assert.True(NodeLinks.Seal(doc, Reachable(Open), Ink));

        var runs = Prose(doc).Runs;
        var at = runs.FindIndex(r => r.Text == "@Diary");
        Assert.Equal(NodeLinks.LockPrefix, runs[at - 1].Text);
        Assert.Equal(Ink.LockedLink, runs[at].Style.Color);
        Assert.False(NodeUrl.TryParse(runs[at].Style.Link, out _));

        // The one the reader may open is left exactly as the page wrote it.
        var live = runs[runs.FindIndex(r => r.Text == "@Notebook")];
        Assert.Equal($"node://{Open}", live.Style.Link);
        Assert.Equal(default, live.Style.Color);
    }

    [Fact]
    public void An_embedded_file_the_reader_cannot_fetch_becomes_its_caption()
    {
        var doc = Page();
        var at = PictureAt(doc);

        NodeLinks.Seal(doc, Reachable(Open), Ink);

        // Left as an image it would be a broken picture and nothing else: the browser
        // fetches it itself and is refused.
        var picture = Picture(doc, at);
        Assert.NotEqual(BlockKind.Image, picture.Kind);
        Assert.Equal("", picture.ImageUrl);
        Assert.Equal(NodeLinks.LockPrefix + "The rack", picture.Text);
        Assert.Equal(Ink.LockedLink, picture.Runs[0].Style.Color);
    }

    [Fact]
    public void Sealing_twice_changes_nothing_the_second_time()
    {
        var doc = Page();

        Assert.True(NodeLinks.Seal(doc, Reachable(Open), Ink));
        Assert.False(NodeLinks.Seal(doc, Reachable(Open), Ink));
        Assert.Equal(1, Prose(doc).Runs.Count(r => r.Text == NodeLinks.LockPrefix));
    }

    [Fact]
    public void A_locked_link_is_re_inked_when_the_mode_changes()
    {
        var dark = ChromeInk.For(isDark: true);
        var doc = Page();
        var at = PictureAt(doc);
        NodeLinks.Seal(doc, Reachable(Open), Ink);

        Assert.True(NodeLinks.Seal(doc, Reachable(Open), dark));

        Assert.All(Prose(doc).Runs.Where(r => r.Text == NodeLinks.LockPrefix),
            r => Assert.Equal(dark.LockedLink, r.Style.Color));
        Assert.Equal(dark.LockedLink, Picture(doc, at).Runs[0].Style.Color);
    }

    [Fact]
    public void A_page_that_answered_for_everything_is_left_alone()
    {
        var doc = Page();

        Assert.False(NodeLinks.Seal(doc, Reachable(Open, Shut), Ink));
        Assert.Equal(BlockKind.Image, doc.Blocks[PictureAt(doc)].Kind);
    }

    [Fact]
    public void The_reader_gets_a_padlock_and_no_target_at_all()
    {
        var doc = Page();
        NodeLinks.Seal(doc, Reachable(Open), Ink);

        var html = RichHtmlWriter.WriteBody(doc, new RichHtmlOptions());

        // locked: is on nobody's allow-list, so the anchor never becomes one — a click
        // has nothing to follow, and neither does a crawler.
        Assert.DoesNotContain("locked:", html);
        Assert.DoesNotContain($"/api/files/{Shut}/content", html);
        Assert.Contains(NodeLinks.LockPrefix, html);
        Assert.Contains("@Notebook", html);
    }
}
