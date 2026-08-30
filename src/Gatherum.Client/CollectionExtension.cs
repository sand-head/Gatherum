using System.Text;
using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>
/// A collaborative collectible list, as a directive fence beside the encyclopedia's own:
/// <code>
/// :::collection Override sprites
/// - Sonic
///   - Base
///   - Gold
/// - [Klombo](node://0193…)
/// :::
///
/// :::collection [Override sprites](node://0193…)
/// - [x] Sonic — Gold, Sprite Day 2
/// :::
/// </code>
/// The first spelling <em>declares</em> a list — the catalogue, what exists to collect.
/// The second, whose argument names another node, <em>tracks</em> it: the page is one
/// person's tally, and the task marks are their ticks. That is the whole of how a page
/// says which of the two it is, and it is exact rather than inferred — an earlier draft
/// recognized a tally by it linking a catalogue and carrying matching task items, which
/// would have counted any page discussing the list with example checkboxes as somebody's
/// column.
///
/// Inside the fence the vocabulary is the dialect's own — bulleted items, nested one
/// level for variants, mentions and <c>[[wiki links]]</c> — parsed by the ordinary
/// parser, so the items reach search, a link in one is a real link the backlinks panel
/// sees, and the canvas paints the run as the blocks it is. What makes this construct
/// different from an aside is only where it is <em>read</em>: the reading view claims
/// the run as a slopedit widget and draws the grid, because a reader wants to work a
/// shared list rather than look at it. The server's half is
/// <see cref="Gatherum.Core.Markdown.CollectionSyntax"/>, and the two have to agree
/// about every line.
/// </summary>
public sealed class CollectionExtension : MarkdownBlockExtension
{
    private const string Fence = ":::";

    public override bool TryRead(IReadOnlyList<string> lines, int at,
        List<Block> into, List<BlockDecoration> decorations, List<FloatedRun> floats,
        IReadOnlyList<MarkdownExtension> siblings, out int consumed)
    {
        consumed = 0;
        if (ArgumentOf(lines[at]) is not { } argument)
            return false;

        var body = new List<string>();
        var i = at + 1;
        while (i < lines.Count && lines[i].Trim() != Fence)
        {
            body.Add(lines[i]);
            i++;
        }
        if (i >= lines.Count)
            return false;                       // unterminated: not ours after all

        var blocks = MarkdownSerializer.Parse(string.Join('\n', body), siblings);
        if (blocks.Count == 0)
            return false;
        var tag = BlockTags.For(argument.Length > 0
            ? $"{BlockTags.Collection} {argument}"
            : BlockTags.Collection);
        foreach (var block in blocks)
            block.Tag = tag;
        into.AddRange(blocks);

        consumed = i - at + 1;                  // the fence lines too
        return true;
    }

    public override bool TryWrite(IReadOnlyList<Block> blocks, int at,
        StringBuilder into, IReadOnlyList<MarkdownExtension> siblings, out int consumed)
    {
        consumed = 0;
        var tag = blocks[at].Tag;
        if (!BlockTags.IsCollection(tag))
            return false;
        var end = at;
        while (end < blocks.Count && blocks[end].Tag == tag)
            end++;

        into.Append(Fence).Append(BlockTags.SourceOf(tag)).Append('\n')
            .Append(AsideExtension.Untagged(blocks, at, end - at, siblings)).Append('\n')
            .Append(Fence);
        consumed = end - at;
        return true;
    }

    /// <summary>The list this fence names, or null when the line opens something else.
    /// The argument is kept as the source spelled it — a name, a <c>[[title]]</c> or a
    /// mention — because the writer has to give it back unchanged, and because which of
    /// the three it is decides what the page is.</summary>
    private static string? ArgumentOf(string line)
    {
        var text = line.Trim();
        if (!text.StartsWith(Fence, StringComparison.Ordinal))
            return null;
        var rest = text[Fence.Length..].TrimStart();
        if (!rest.StartsWith(BlockTags.Collection, StringComparison.OrdinalIgnoreCase))
            return null;
        rest = rest[BlockTags.Collection.Length..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
            return null;                        // ":::collections" is somebody else's
        return rest.Trim();
    }
}
