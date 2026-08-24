using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>
/// The other half of a link's honesty. A page is free to link a node its reader may not
/// open — a public page pointing at its author's private file is the ordinary case, not
/// the exotic one — and the id is written into the page either way, so dropping the link
/// would misreport what the page says while leaving it live would misreport where it
/// goes. A link nobody answered for is drawn as what it is: greyed, prefixed with a
/// padlock, and going nowhere.
///
/// Which links those are is a question only the server can answer, and the answer is the
/// reader's rather than the page's — the same shape as <see cref="WikiLinks"/>, one
/// question later: a wiki link asks whether a title names anything, a mention already
/// knows the id and asks whether this particular visitor may follow it.
///
/// Reading only. Sealing rewrites runs and inserts text, and a document that can be
/// saved has to write back the bytes it was read from.
/// </summary>
public static class NodeLinks
{
    /// <summary>What a locked link wears. The space is non-breaking so the padlock never
    /// wraps away from the words it belongs to.</summary>
    public const string LockPrefix = "\U0001F512\u00A0";

    /// <summary>A link that has been sealed. Deliberately not a scheme anything knows:
    /// the HTML emitter's allow-list rejects it so the anchor loses its href,
    /// <see cref="LinkRouter"/> routes it Nowhere, and the id stays legible so a theme
    /// change can find the run again and re-ink it.</summary>
    private const string LockedScheme = "locked:";

    /// <summary>Every node this document links or embeds, de-duplicated — what the
    /// reader asks the server about before it draws them.</summary>
    public static IReadOnlyList<Guid> TargetsIn(RichDocument document)
    {
        var targets = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var block in document.Blocks)
        {
            if (block.Kind == BlockKind.Image && NodeUrl.TryParse(block.ImageUrl, out var picture)
                && seen.Add(picture))
                targets.Add(picture);
            foreach (var run in block.Runs)
            {
                if (NodeUrl.TryParse(run.Style.Link, out var target) && seen.Add(target))
                    targets.Add(target);
            }
        }
        return targets;
    }

    /// <summary>Lock every link and embedded picture whose node did not come back
    /// reachable, and re-ink the ones already locked for the mode they are now being read
    /// in. True when anything actually changed — the caller only has to repaint then.</summary>
    public static bool Seal(RichDocument document, IReadOnlySet<Guid> reachable, ChromeInk ink)
    {
        var changed = false;
        foreach (var block in document.Blocks)
        {
            if (SealPicture(block, reachable, ink))
            {
                changed = true;
                continue;
            }
            for (var r = 0; r < block.Runs.Count; r++)
                changed |= SealRun(block, ref r, reachable, ink);
        }
        if (changed)
            document.InvalidateLayout();
        return changed;
    }

    /// <summary>An embedded file the reader may not fetch would be a broken image and
    /// nothing else — the browser goes and gets it, and gets a 404. So the picture
    /// becomes its caption instead: the alt text, locked, in the flow where it stood.</summary>
    private static bool SealPicture(Block block, IReadOnlySet<Guid> reachable, ChromeInk ink)
    {
        if (block.Kind != BlockKind.Image || !NodeUrl.TryParse(block.ImageUrl, out var picture)
            || reachable.Contains(picture))
            return false;

        var label = block.ImageAlt is { Length: > 0 } alt ? alt : "Private file";
        block.Kind = BlockKind.Paragraph;
        block.ImageUrl = "";
        block.Runs.Clear();
        block.Runs.Add(new StyledRun(LockPrefix + label, LockedStyle(picture, ink)));
        return true;
    }

    /// <summary>Advances <paramref name="index"/> past the padlock it inserts, so the
    /// caller's walk does not read the prefix as a run of its own.</summary>
    private static bool SealRun(Block block, ref int index, IReadOnlySet<Guid> reachable,
        ChromeInk ink)
    {
        var run = block.Runs[index];
        if (LockedIdOf(run.Style.Link) is not null)
        {
            if (run.Style.Color == ink.LockedLink)
                return false;
            block.Runs[index] = run with { Style = run.Style with { Color = ink.LockedLink } };
            return true;
        }
        if (!NodeUrl.TryParse(run.Style.Link, out var target) || reachable.Contains(target))
            return false;

        block.Runs[index] = run with
        {
            Style = run.Style with { Link = LockedScheme + target, Color = ink.LockedLink },
        };
        if (WearsPadlock(block, index - 1, target))
            return true;
        block.Runs.Insert(index, new StyledRun(LockPrefix, LockedStyle(target, ink)));
        index++;
        return true;
    }

    /// <summary>Whether the run before a locked one is already its padlock — which is how
    /// a link split across several runs (a bold word inside it, say) gets one.</summary>
    private static bool WearsPadlock(Block block, int index, Guid target) =>
        index >= 0 && block.Runs[index].Text == LockPrefix
        && LockedIdOf(block.Runs[index].Style.Link) == target;

    private static InlineStyle LockedStyle(Guid nodeId, ChromeInk ink) =>
        InlineStyle.Plain with { Link = LockedScheme + nodeId, Color = ink.LockedLink };

    private static Guid? LockedIdOf(string? link) =>
        link is not null && link.StartsWith(LockedScheme, StringComparison.Ordinal)
        && Guid.TryParse(link[LockedScheme.Length..], out var id)
            ? id
            : null;
}
