using Gatherum.Client;
using SlopEdit.Core.Rich;

namespace Gatherum.Tests;

/// <summary>Archive-backed citations: a footnote whose note cites a node, and — when
/// that node is a bookmark — the capture that backed the claim, dated, with the source's
/// own address trailing it. No new syntax: the note is a mention, prose, and a link,
/// which is why everything here is the dialect proving it already speaks citation.</summary>
public class CitationTests
{
    private static readonly Guid Bookmark = Guid.Parse("8f6b1f5e-9a5a-4a2e-9d16-6b8a1c2d3e4f");
    private static readonly DateTimeOffset Captured =
        new(2026, 8, 27, 3, 14, 0, TimeSpan.Zero);

    [Fact]
    public void A_bookmark_cites_with_its_capture_date_and_its_source()
    {
        var runs = Citation.Runs("Example Domain", Bookmark,
            "https://example.com/some/page", Captured);

        Assert.Equal(
            ["Example Domain", ", captured 27 August 2026", " — ", "example.com", "."],
            runs.Select(r => r.Text));
        Assert.Equal($"node://{Bookmark}", runs[0].Style.Link);
        Assert.Equal("https://example.com/some/page", runs[3].Style.Link);
        Assert.All(new[] { runs[1], runs[2], runs[4] }, r => Assert.Null(r.Style.Link));
    }

    [Fact]
    public void The_capture_day_is_the_UTC_day_whatever_offset_stamped_it()
    {
        // 05:00 on the 28th in Sydney is still the 27th in UTC, and the date is prose
        // both readers have to agree about.
        var runs = Citation.Runs("Example Domain", Bookmark, "https://example.com",
            new DateTimeOffset(2026, 8, 28, 5, 0, 0, TimeSpan.FromHours(10)));

        Assert.Contains(runs, r => r.Text == ", captured 27 August 2026");
    }

    [Fact]
    public void A_node_that_is_no_bookmark_cites_as_the_mention_alone()
    {
        var runs = Citation.Runs("Thermals", Bookmark, sourceUrl: null, capturedAt: null);

        Assert.Equal(["Thermals", "."], runs.Select(r => r.Text));
        Assert.Equal($"node://{Bookmark}", runs[0].Style.Link);
    }

    /// <summary>The editor's whole insertion, in miniature: the model places the marker
    /// and picks the key, the fresh note is given the citation's runs, and what lands in
    /// the file is one plain footnote line that reads back byte-stable.</summary>
    [Fact]
    public void A_citation_is_a_footnote_in_the_file_and_survives_the_round_trip()
    {
        var doc = GatherumMarkdown.Parse("The page moved on.", isDark: false);
        doc.MoveTo(0, "The page moved on.".Length, extend: false);
        var key = doc.InsertFootnote();
        var note = doc.Blocks.First(b => b.Footnote == key);
        note.Runs.Clear();
        foreach (var run in Citation.Runs("Example Domain", Bookmark,
            "https://example.com/some/page", Captured))
            note.Runs.Add(run);

        var written = GatherumMarkdown.ToMarkdown(doc);

        Assert.Contains("The page moved on.[^1]", written);
        Assert.Contains($"[^1]: [Example Domain](node://{Bookmark}), " +
            "captured 27 August 2026 — [example.com](https://example.com/some/page).",
            written);
        Assert.Equal(written,
            GatherumMarkdown.ToMarkdown(GatherumMarkdown.Parse(written, isDark: false)));
    }

    /// <summary>A footnote's note is blocks like any other, so the mention in one is
    /// asked about and, for a reader who may not open the cited node, padlocked — a
    /// citation into somebody's private bookmark says so instead of leading into a 404.</summary>
    [Fact]
    public void The_cited_node_locks_for_a_reader_who_may_not_open_it()
    {
        var doc = GatherumMarkdown.Parse($"""
            The page moved on.[^1]

            [^1]: [Example Domain](node://{Bookmark}), captured 27 August 2026 — [example.com](https://example.com/some/page).
            """, isDark: false);

        Assert.Equal([Bookmark], NodeLinks.TargetsIn(doc));

        var ink = ChromeInk.For(isDark: false);
        Assert.True(NodeLinks.Address(doc, new HashSet<Guid>(), ink));
        var cited = doc.Blocks.First(b => b.Footnote is { Length: > 0 })
            .Runs.First(r => r.Text == "Example Domain");
        Assert.Equal($"{NodeLinks.LockedScheme}{Bookmark}", cited.Style.Link);
        Assert.Equal(ink.LockedLink, cited.Style.Color);
    }
}
