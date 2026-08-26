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

    /// <summary>The HTML the read view asks for, which is the whole point of the
    /// exercise: the padlock is a stylesheet rule, and a rule needs an element.</summary>
    private static RichHtmlOptions ReaderOptions() =>
        new() { AllowedUrlSchemes = NodeLinks.ReaderUrlSchemes };

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

        Assert.True(NodeLinks.Address(doc, Reachable(Open), Ink));

        var runs = Prose(doc).Runs;
        var locked = runs[runs.FindIndex(r => r.Text == "@Diary")];
        Assert.Equal(Ink.LockedLink, locked.Style.Color);
        Assert.Equal($"{NodeLinks.LockedScheme}{Shut}", locked.Style.Link);
        Assert.False(NodeUrl.TryParse(locked.Style.Link, out _));

        // The padlock is a picture the stylesheet hangs on that scheme, not a character
        // in the page: nothing about the text the author wrote has changed.
        Assert.Equal("@Diary", locked.Text);

        // The one the reader may open gets the address that opens it, and keeps the ink
        // the page wrote it in.
        var live = runs[runs.FindIndex(r => r.Text == "@Notebook")];
        Assert.Equal($"/nodes/{Open}", live.Style.Link);
        Assert.Equal(default, live.Style.Color);
    }

    [Fact]
    public void An_embedded_file_the_reader_cannot_fetch_becomes_its_caption()
    {
        var doc = Page();
        var at = PictureAt(doc);

        NodeLinks.Address(doc, Reachable(Open), Ink);

        // Left as an image it would be a broken picture and nothing else: the browser
        // fetches it itself and is refused.
        var picture = Picture(doc, at);
        Assert.NotEqual(BlockKind.Image, picture.Kind);
        Assert.Equal("", picture.ImageUrl);
        Assert.Equal("The rack", picture.Text);
        Assert.Equal($"{NodeLinks.LockedScheme}{Shut}", picture.Runs[0].Style.Link);
        Assert.Equal(Ink.LockedLink, picture.Runs[0].Style.Color);
    }

    [Fact]
    public void A_mention_the_reader_may_follow_is_a_real_link()
    {
        var doc = Page();

        NodeLinks.Address(doc, Reachable(Open), Ink);
        var html = RichHtmlWriter.WriteBody(doc, ReaderOptions());

        // The bug this exists for: node:// is nobody's scheme, so the read view dropped
        // it and a mention came out as a styled span — no href, nothing to click, nothing
        // to open in a new tab, nothing for the status bar to say.
        Assert.Contains($"<a class=\"se-link\" href=\"/nodes/{Open}\"", html);
        Assert.DoesNotContain("node://", html);
    }

    [Fact]
    public void A_mention_nobody_answered_for_stays_inert()
    {
        // The reader leaves the document alone when the server does not answer, and that
        // has to mean "no link", not "a link into somebody's private node".
        var html = RichHtmlWriter.WriteBody(Page(), ReaderOptions());

        Assert.DoesNotContain("<a", html);
        Assert.Contains("@Notebook", html);
    }

    [Fact]
    public void Addressing_twice_changes_nothing_the_second_time()
    {
        var doc = Page();

        Assert.True(NodeLinks.Address(doc, Reachable(Open), Ink));
        Assert.False(NodeLinks.Address(doc, Reachable(Open), Ink));
    }

    [Fact]
    public void A_locked_link_is_re_inked_when_the_mode_changes()
    {
        var dark = ChromeInk.For(isDark: true);
        var doc = Page();
        var at = PictureAt(doc);
        NodeLinks.Address(doc, Reachable(Open), Ink);

        Assert.True(NodeLinks.Address(doc, Reachable(Open), dark));

        Assert.All(Prose(doc).Runs.Where(r => r.Style.Link?.StartsWith(NodeLinks.LockedScheme) == true),
            r => Assert.Equal(dark.LockedLink, r.Style.Color));
        Assert.Equal(dark.LockedLink, Picture(doc, at).Runs[0].Style.Color);
    }

    [Fact]
    public void A_page_that_answered_for_everything_padlocks_nothing()
    {
        var doc = Page();
        var at = PictureAt(doc);

        NodeLinks.Address(doc, Reachable(Open, Shut), Ink);

        Assert.Equal(BlockKind.Image, doc.Blocks[at].Kind);
        Assert.All(Prose(doc).Runs.Where(r => r.Style.Link is { Length: > 0 }),
            r => Assert.StartsWith("/nodes/", r.Style.Link!));
        Assert.DoesNotContain(Prose(doc).Runs, r => r.Style.Color == Ink.LockedLink);
    }

    [Fact]
    public void The_read_views_allow_list_adds_ours_to_slopedits_own()
    {
        // Read off slopedit rather than restated, so a scheme it starts keeping keeps
        // working here without anyone noticing this list exists.
        Assert.Equal([.. new RichHtmlOptions().AllowedUrlSchemes, "locked"],
            NodeLinks.ReaderUrlSchemes);
    }

    [Fact]
    public void The_reader_gets_an_anchor_to_padlock_and_no_target_at_all()
    {
        var doc = Page();
        NodeLinks.Address(doc, Reachable(Open), Ink);

        var html = RichHtmlWriter.WriteBody(doc, ReaderOptions());

        // An anchor, because the stylesheet needs something to hang the padlock on — and
        // one no browser can follow: locked: is nobody's protocol, the reader's click
        // delegate routes it Nowhere, and the file's own URL is gone from the page.
        Assert.Contains($"href=\"{NodeLinks.LockedScheme}{Shut}\"", html);
        Assert.DoesNotContain($"/api/files/{Shut}/content", html);
        Assert.DoesNotContain("\U0001F512", html);
        Assert.Contains("@Notebook", html);
    }
}
