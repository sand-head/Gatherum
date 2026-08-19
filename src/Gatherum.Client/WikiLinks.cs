using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>
/// The <c>[[Title]]</c> side of a page: which titles a document links, and which of
/// them point at nothing yet.
///
/// A wiki link names a page instead of pointing at one, so whether it goes anywhere is
/// a question only the server can answer — and the answer changes when someone else
/// creates the page. The editor asks after loading and after saving, then inks the runs
/// that resolved to nothing in <see cref="ChromeInk.DeadLink"/>: the red link every
/// wiki has, an invitation rather than an error.
/// </summary>
public static class WikiLinks
{
    /// <summary>Every title this document wiki-links, de-duplicated case-insensitively
    /// the way resolution matches them.</summary>
    public static IReadOnlyList<string> TargetsIn(RichDocument document)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in document.Blocks)
        {
            foreach (var run in block.Runs)
            {
                if (WikiLinkExtension.TargetOf(run.Style.Link ?? "") is { Length: > 0 } target &&
                    seen.Add(target))
                    targets.Add(target);
            }
        }
        return targets;
    }

    /// <summary>Ink every wiki link by whether its title resolved: dead ones red, live
    /// ones back to the theme's link color. True when anything actually changed — the
    /// caller only has to repaint then.</summary>
    public static bool Mark(RichDocument document, IReadOnlySet<string> live, ChromeInk ink)
    {
        var changed = false;
        foreach (var block in document.Blocks)
        {
            for (var r = 0; r < block.Runs.Count; r++)
            {
                var run = block.Runs[r];
                if (WikiLinkExtension.TargetOf(run.Style.Link ?? "") is not { Length: > 0 } target)
                    continue;
                var color = live.Contains(target) ? default : ink.DeadLink;
                if (run.Style.Color == color)
                    continue;
                block.Runs[r] = run with { Style = run.Style with { Color = color } };
                changed = true;
            }
        }
        return changed;
    }
}
