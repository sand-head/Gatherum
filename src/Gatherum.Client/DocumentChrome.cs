using SlopEdit.Core.Rich;
using SlopEdit.Core.Text;

namespace Gatherum.Client;

/// <summary>
/// Where a page's constructs get their geometry and their paint: every run of blocks
/// sharing a <see cref="BlockTags">tag</see> becomes a float (an aside leaves the
/// vertical flow and the prose wraps past it) and a decoration (the card behind it).
///
/// Derived, never pinned. slopedit declares both against block indices, and block
/// indices move the moment anyone types a paragraph above them — so the answer is
/// recomputed from the tags after every change and every theme switch, rather than
/// installed once at parse. That also makes the extensions colorblind: they say what a
/// construct is, this says what it currently looks like.
/// </summary>
public static class DocumentChrome
{
    private const float InfoboxWidthPx = 280f;
    private const float FigureWidthPx = 320f;

    public static void Apply(RichDocument document, bool isDark)
    {
        var ink = ChromeInk.For(isDark);
        var floats = new List<FloatedRun>();
        var boxes = new List<BlockDecoration>();
        var blocks = document.Blocks;

        for (var i = 0; i < blocks.Count;)
        {
            var tag = blocks[i].Tag;
            if (BlockTags.KindOf(tag) is not { } kind)
            {
                i++;
                continue;
            }
            var end = i;
            while (end < blocks.Count && blocks[end].Tag == tag)
                end++;

            if (BlockTags.IsAside(tag))
                Aside(blocks, i, end - i, kind, BlockTags.ArgumentsOf(tag), ink, floats, boxes);
            else if (BlockTags.IsCallout(tag))
                Callout(blocks, i, end - i, BlockTags.ArgumentsOf(tag), ink, boxes);
            i = end;
        }

        // Declaring is a relayout; say nothing when nothing moved, because this runs on
        // every keystroke.
        if (!document.Floats.SequenceEqual(floats))
            document.SetFloats([.. floats]);
        if (!document.Decorations.SequenceEqual(boxes))
            document.SetDecorations([.. boxes]);
    }

    /// <summary>An infobox or a figure: a column at one margin, a card behind the whole
    /// run, and — for an infobox — a band behind each heading, which is most of what
    /// makes one look like one.</summary>
    private static void Aside(IReadOnlyList<Block> blocks, int first, int count, string kind,
        string[] arguments, ChromeInk ink, List<FloatedRun> floats, List<BlockDecoration> boxes)
    {
        var side = FloatSide.Right;
        var width = kind == BlockTags.Figure ? FigureWidthPx : InfoboxWidthPx;
        foreach (var argument in arguments)
        {
            if (argument.Equals("left", StringComparison.OrdinalIgnoreCase))
                side = FloatSide.Left;
            else if (argument.Equals("right", StringComparison.OrdinalIgnoreCase))
                side = FloatSide.Right;
            else if (float.TryParse(argument, out var given) && given >= 120f)
                width = Math.Min(given, 640f);
        }

        // Wikipedia gives an infobox `margin: 0.5em 0 0.5em 1em`: the 1em is the
        // gutter, and the 0.5em of air above and below — so the card touches neither
        // the heading over it nor the prose that clears it — is the margins.
        floats.Add(new FloatedRun(first, count, side, width, GutterPx: 20f,
            TopMarginPx: 8f, BottomMarginPx: 8f));
        boxes.Add(new BlockDecoration(first, count, Background: ink.CardFill,
            Border: ink.CardBorder, BorderWidth: 1f, PadPx: 8f));
        if (kind != BlockTags.Infobox)
            return;
        for (var b = first; b < first + count; b++)
        {
            if (blocks[b].Kind == BlockKind.Heading)
                boxes.Add(new BlockDecoration(b, 1, Background: ink.Band, PadPx: 3f));
        }
    }

    /// <summary>A callout: a tinted card in its kind's accent, with the title line
    /// inked to match. The title's color is set here rather than at parse for the same
    /// reason the card is — the mode can change under a document that is already open.</summary>
    private static void Callout(IReadOnlyList<Block> blocks, int first, int count,
        string[] arguments, ChromeInk ink, List<BlockDecoration> boxes)
    {
        var (fill, border, titleInk) = ink.Callout(arguments is [var kind] ? kind : "note");
        boxes.Add(new BlockDecoration(first, count, Background: fill, Border: border,
            BorderWidth: 1f, PadPx: 8f));
        Recolor(blocks[first], titleInk);
    }

    /// <summary>Repaint a block's runs, leaving the text and its flags alone. Assigning
    /// only what differs keeps a per-keystroke pass from churning the run list.</summary>
    private static void Recolor(Block block, CellColor color)
    {
        for (var r = 0; r < block.Runs.Count; r++)
        {
            var run = block.Runs[r];
            if (run.Style.Color != color)
                block.Runs[r] = run with { Style = run.Style with { Color = color } };
        }
    }
}
