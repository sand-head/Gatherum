using SlopEdit.Core.Rich;
using SlopEdit.Core.Text;

namespace Gatherum.Client;

/// <summary>
/// Where a page's constructs get their geometry and their paint: every run of blocks
/// sharing a <see cref="BlockTags">tag</see> becomes a float (an aside leaves the
/// vertical flow and the prose wraps past it) and a decoration (the card behind it).
///
/// Derived, never pinned. slopedit anchors declared ranges through every splice now
/// (2.5.11) — an edit above an infobox no longer slides its card — but the tags stay
/// the one source of truth for what a construct <em>is</em>: recomputing from them
/// after every change and every theme switch keeps membership honest at the seams a
/// splice cannot speak to (a paste that brings tagged blocks with it, a construct
/// deleted whole), restamps the small print on blocks typed into a card, and re-inks
/// a callout title whose runs an edit replaced. The extensions stay colorblind: they
/// say what a construct is, this says what it currently looks like.
/// </summary>
public static class DocumentChrome
{
    private const float InfoboxWidthPx = 280f;
    private const float FigureWidthPx = 320f;

    // The app's card, in the vocabulary a decoration speaks since slopedit 2.6.0.
    // Every inset block in Gatherum is a tonal fill behind a rounded hairline — the
    // content sheet at --radius-l, a code band at --radius-s — and an aside is one of
    // those, sized between the two. Roomier flanks than crown, because a 280px column
    // of small print is read down its middle and the card should not crowd it.
    private const float CardRadiusPx = 12f;             // --radius
    private static readonly BoxEdges CardPad = BoxEdges.Symmetric(10f, 12f);

    /// <summary>The page margin both document surfaces hand slopedit as
    /// <c>ContentPadding</c>, and lean back into the pane so the text column does not
    /// move (app.css). A decoration may not outset past the page's edge — the box
    /// would be drawn nowhere — so an aside floated flush at a margin can only pad the
    /// side the page has room on: at zero, a right-floated infobox pads its card on
    /// the left and not on the right, and its rule sits visibly off-centre. This is
    /// the room that buys the other side, which is why it must not be less than the
    /// card's own horizontal padding (<see cref="PageMarginCoversTheCard"/> keeps it
    /// honest). It doubles as the gutter a heading's fold chevron hangs in.</summary>
    public const float PagePaddingPx = 24f;

    /// <summary>The card's horizontal outset, for the invariant above.</summary>
    public static float CardSidePaddingPx => CardPad.Left;

    /// <summary>Whether the page margin can cover the card's outset on both sides.</summary>
    public static bool PageMarginCoversTheCard =>
        PagePaddingPx >= MathF.Max(CardPad.Left, CardPad.Right);


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
            else if (BlockTags.IsSharedList(tag))
                SharedList(i, end - i, ink, boxes);
            i = end;
        }

        // Declaring is a relayout; say nothing when nothing moved, because this runs on
        // every keystroke.
        if (!document.Floats.SequenceEqual(floats))
            document.SetFloats([.. floats]);
        if (!document.Decorations.SequenceEqual(boxes))
            document.SetDecorations([.. boxes]);
    }

    /// <summary>An infobox or a figure: a column at one margin and the app's card
    /// behind the whole run — a tonal fill inside a rounded hairline, the same recipe
    /// the content sheet and a code band are drawn with, at a radius between theirs.
    /// An infobox's title takes the accent its rows are filed under.</summary>
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
            Border: ink.CardBorder, BorderWidth: 1f, Padding: CardPad,
            CornerRadiusPx: CardRadiusPx));
        for (var b = first; b < first + count; b++)
        {
            // The card is small print wherever its blocks came from: parse stamps the
            // scale, this keeps it on blocks an edit added — Enter splitting a row,
            // a paragraph typed under the picture. (An edit invalidates layout
            // anyway, so restating the same value costs nothing.)
            blocks[b].FontScale = AsideExtension.SmallPrint;
            // The title is the app's accent over the hairline the heading already
            // rules, rather than an encyclopedia's tinted band. A band wants to reach
            // the card's edges, and a card with rounded corners has no edges to reach
            // — inset it and it reads as a chip that missed, keep the tint and the
            // rule and you get two dividers doing one job. So: no band, the chip's own
            // ink on the title, and that rule is the divider. Inked here rather than
            // at parse for the reason the card is — the mode can change under a
            // document that is already open.
            if (kind == BlockTags.Infobox && blocks[b].Kind == BlockKind.Heading)
                Recolor(blocks[b], ink.OnBand);
        }
    }

    /// <summary>A callout: a tinted card in its kind's accent, with the title line
    /// inked to match. The title's color is set here rather than at parse for the same
    /// reason the card is — the mode can change under a document that is already open.</summary>
    private static void Callout(IReadOnlyList<Block> blocks, int first, int count,
        string[] arguments, ChromeInk ink, List<BlockDecoration> boxes)
    {
        var (fill, border, titleInk) = ink.Callout(arguments is [var kind] ? kind : "note");
        // Same card as an aside wears, in the kind's accent — two constructs that sit
        // in one page should not disagree about what a card is.
        boxes.Add(new BlockDecoration(first, count, Background: fill, Border: border,
            BorderWidth: 1f, Padding: CardPad, CornerRadiusPx: CardRadiusPx));
        Recolor(blocks[first], titleInk);
    }

    /// <summary>A shared list, on the surface that edits it: the app's card over the
    /// plain list, which is the right thing to see while rearranging a shared roster —
    /// rather than a live grid of other people's data laid over the thing being edited.
    /// The reading view claims this run as a widget and stands the box down, so the
    /// decoration declared here is the canvas's alone.</summary>
    private static void SharedList(int first, int count, ChromeInk ink,
        List<BlockDecoration> boxes) =>
        boxes.Add(new BlockDecoration(first, count, Background: ink.CardFill,
            Border: ink.CardBorder, BorderWidth: 1f, Padding: CardPad,
            CornerRadiusPx: CardRadiusPx));

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
