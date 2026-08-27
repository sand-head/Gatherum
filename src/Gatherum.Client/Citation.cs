using System.Globalization;
using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>
/// A citation is not new syntax: it is a footnote whose note cites a node, written in
/// the vocabulary the dialect already has. For a bookmark that note carries what makes
/// a reference outlive the web it came from — the mention opens the capture Gatherum
/// keeps, the date says which capture backed the claim, and the source's own address
/// trails it the way "archived from the original" trails a reference that expects the
/// original to rot. In the file it is one plain Markdown line:
/// <c>[^1]: [Title](node://id), captured 27 August 2026 — [example.com](https://…).</c>
/// </summary>
public static class Citation
{
    /// <summary>The note's runs. The mention is labeled with the bare title — a
    /// reference list names its source, it doesn't @-address it — and it is still a
    /// mention by URL, so it backlinks, locks, and survives a rename exactly like one.
    /// A node with no source URL (a page, an uploaded file) cites as the mention alone:
    /// only a capture has a date worth quoting.</summary>
    public static IReadOnlyList<StyledRun> Runs(string title, Guid nodeId,
        string? sourceUrl, DateTimeOffset? capturedAt)
    {
        var runs = new List<StyledRun>
        {
            new(title, InlineStyle.Plain with { Link = $"node://{nodeId}" }),
        };
        if (sourceUrl is { Length: > 0 } &&
            Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source))
        {
            if (capturedAt is { } at)
                runs.Add(new StyledRun($", captured {Day(at)}", InlineStyle.Plain));
            runs.Add(new StyledRun(" — ", InlineStyle.Plain));
            runs.Add(new StyledRun(source.Host, InlineStyle.Plain with { Link = sourceUrl }));
        }
        runs.Add(new StyledRun(".", InlineStyle.Plain));
        return runs;
    }

    /// <summary>The capture's day, spelled out and in UTC — the date is prose that has
    /// to mean the same thing to both readers, whatever locale either browser is in.</summary>
    private static string Day(DateTimeOffset at) =>
        at.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
}
