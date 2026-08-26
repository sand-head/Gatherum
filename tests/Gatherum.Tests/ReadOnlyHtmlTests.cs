using Gatherum.Client;
using SlopEdit.Core.Rich;

namespace Gatherum.Tests;

/// <summary>
/// A page as the read-only view renders it. slopedit's own parity suite promises the
/// HTML and the canvas agree about everything the editor knows; what it cannot know is
/// the vocabulary Gatherum grafts on per call — an infobox's float and card, a callout's
/// tint, a wiki link's URL — so this is the host's half of that promise. Pure model work:
/// the emitter is browser-free and answers without a measurer.
/// </summary>
public class ReadOnlyHtmlTests
{
    private static string Html(string markdown, bool isDark = false) =>
        RichHtmlWriter.WriteBody(GatherumMarkdown.Parse(markdown, isDark), new RichHtmlOptions());

    [Fact]
    public void The_wikis_own_constructs_reach_the_reader()
    {
        var html = Html("""
            # Podman

            A [[Homelab]] note.

            :::infobox
            # Podman
            | **Kind** | Container engine |
            :::

            > [!WARNING]
            > Rootless containers need lingering.

            ![A diagram](/api/files/8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f/content)

            ```sh
            podman run
            ```
            """);

        // An aside leaves the flow, and the card and header band it is dressed with come
        // with it — chrome derived from block tags, not anything slopedit ships.
        Assert.Contains("<aside style=\"float:right", html);
        // The 0.5em of air Wikipedia keeps above an infobox; the bottom half rides
        // the aside's margin-bottom, folded into the paragraph gap.
        Assert.Contains("margin-top:8px", html);
        Assert.Contains("data-tag=\"infobox", html);
        Assert.Contains("data-tag=\"callout warning", html);
        Assert.Contains("Rootless containers need lingering.", html);

        // A wiki link keeps its URL: wikilink: is on the emitter's allow-list, so the
        // browser gets a real anchor and the host's click delegate can still claim it.
        Assert.Contains("<a class=\"se-link\" href=\"wikilink:Homelab\"", html);

        // The things a canvas could only paint: a picture the browser fetches and caches
        // itself, and code the reader can select a line out of.
        Assert.Contains("<img class=\"se-img\" loading=\"lazy\"", html);
        Assert.Contains("/api/files/8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f/content", html);
        Assert.Contains("<code class=\"language-sh\">", html);
    }

    [Fact]
    public void A_link_the_browser_cannot_be_trusted_with_keeps_its_look_and_loses_its_target()
    {
        var html = Html("""
            [@Sam](node://8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f) and [trouble](javascript:alert(1))
            """);

        Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("node://", html);
        // Both still read as links, exactly as the canvas paints them — a mention simply
        // has nowhere to go in a view that isn't routing clicks.
        Assert.Contains("<span class=\"se-link\"", html);
        Assert.Contains("@Sam", html);
    }

    [Fact]
    public void Pages_wear_the_encyclopedias_dress()
    {
        var html = Html("""
            # Podman

            ## History

            ### Origins
            """);

        // The hairline under h1 and h2 — Wikipedia's dress, put on by Dress for every
        // page — and nothing under h3, where the encyclopedia's own stops.
        Assert.Contains("<h1 class=\"se-t se-hline\"", html);
        Assert.Contains("<h2 class=\"se-t se-hline\"", html);
        Assert.Contains("<h3 class=\"se-t\"", html);
    }

    [Fact]
    public void A_phones_column_folds_sections_the_way_wikipedia_mobile_does()
    {
        var doc = GatherumMarkdown.Parse("""
            # Podman

            A container engine.

            ## History

            It began as a CLI for CRI-O.
            """, isDark: false);
        doc.Measurer = new FakeMeasurer();
        var reader = new RichHtmlOptions { CollapseSectionsBelowPx = NodeReader.FoldSectionsBelowPx };

        // A phone's article column: each h2 section folds behind its heading band.
        doc.WrapWidthPx = 360f;
        var narrow = RichHtmlWriter.WriteBody(doc, reader);
        Assert.Contains("<details class=\"se-sec\" open", narrow);
        Assert.Contains("</h2></summary>", narrow);

        // A desktop measure: the fold floor leaves the markup byte-for-byte what it
        // is with no floor at all — folding is phone chrome, not a reinterpretation.
        doc.WrapWidthPx = 800f;
        doc.InvalidateLayout();
        Assert.Equal(RichHtmlWriter.WriteBody(doc, new RichHtmlOptions()),
            RichHtmlWriter.WriteBody(doc, reader));
    }

    [Fact]
    public void A_footnote_reads_as_a_superscript_link_and_its_note_links_back()
    {
        var html = Html("""
            The NAS reboots nightly.[^why]

            [^why]: The controller wedges; see [[Homelab]].
            """);

        // The marker is an anchor down to the note; the note carries the id it lands
        // on and its number is the anchor back up. All slopedit's own plumbing — what
        // is Gatherum's is that it arrives with the wiki's extensions active.
        Assert.Contains("class=\"se-fnref\"", html);
        Assert.Contains("class=\"se-t se-fn\"", html);
        Assert.Contains("class=\"se-marker se-fnmark\"", html);
        Assert.Contains("href=\"wikilink:Homelab\"", html);
    }

    /// <summary>Fixed hand-picked widths, the way slopedit's own layout tests measure:
    /// enough for the emitter to lay out in pixels without a font engine anywhere.</summary>
    private sealed class FakeMeasurer : ITextMeasurer
    {
        public float LineHeight => 20f;
        public float Baseline => 15f;

        public float[] Advances(string text, InlineFlags flags, bool code)
        {
            var advances = new float[text.Length];
            Array.Fill(advances, 8f);
            return advances;
        }
    }

    [Fact]
    public void Chrome_follows_the_theme_into_the_html()
    {
        const string callout = """
            > [!NOTE]
            > Two modes, one document.
            """;

        Assert.NotEqual(Html(callout, isDark: false), Html(callout, isDark: true));
    }
}
