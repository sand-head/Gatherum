using System.Text;
using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>
/// GitHub's alert spelling — a quote whose first line names a kind:
/// <code>
/// > [!WARNING] Restores are not backups
/// > Old bytes survive, but a deleted node's do not.
/// </code>
/// It reads as ordinary quote blocks tagged <c>callout &lt;kind&gt;</c>, so the editor
/// paints its quote bar and <see cref="DocumentChrome"/> adds the tinted card and the
/// kind's accent. The title line is the callout's own; when it says nothing but the
/// kind's name it is written back as the bare <c>[!KIND]</c> marker it came from. An
/// unknown kind is left alone, because a quote that happens to start with a bracket is
/// still just a quote.
/// </summary>
public sealed class CalloutExtension : MarkdownBlockExtension
{
    /// <summary>The five GitHub spells, in the order a reader meets them; the value is
    /// the title a bare marker gets.</summary>
    public static readonly IReadOnlyDictionary<string, string> Kinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["note"] = "Note",
            ["tip"] = "Tip",
            ["important"] = "Important",
            ["warning"] = "Warning",
            ["caution"] = "Caution",
        };

    public override bool TryRead(IReadOnlyList<string> lines, int at,
        List<Block> into, List<BlockDecoration> decorations, List<FloatedRun> floats,
        IReadOnlyList<MarkdownExtension> siblings, out int consumed)
    {
        consumed = 0;
        if (!TryReadMarker(lines[at], out var kind, out var title))
            return false;

        var body = new List<string>();
        var i = at + 1;
        // A second marker starts a second callout rather than being quoted inside this
        // one, even without the blank line Markdown would want between two quotes.
        while (i < lines.Count && lines[i].TrimStart().StartsWith('>') &&
            !TryReadMarker(lines[i], out _, out _))
        {
            body.Add(lines[i]);
            i++;
        }

        var blocks = new List<Block> { TitleBlock(title, siblings) };
        // The body keeps its own '>' markers so the ordinary parser reads it as the
        // quote it is — an extension says what a construct is made of, it doesn't
        // reimplement Markdown.
        blocks.AddRange(MarkdownSerializer.Parse(string.Join('\n', body), siblings));

        var tag = BlockTags.For($"{BlockTags.Callout} {kind}");
        foreach (var block in blocks)
            block.Tag = tag;
        into.AddRange(blocks);

        consumed = i - at;
        return true;
    }

    public override bool TryWrite(IReadOnlyList<Block> blocks, int at,
        StringBuilder into, IReadOnlyList<MarkdownExtension> siblings, out int consumed)
    {
        consumed = 0;
        var tag = blocks[at].Tag;
        if (!BlockTags.IsCallout(tag) || BlockTags.ArgumentsOf(tag) is not [var kind])
            return false;
        var end = at;
        while (end < blocks.Count && blocks[end].Tag == tag)
            end++;

        var title = TitleSource(blocks[at], siblings);
        into.Append("> [!").Append(kind.ToUpperInvariant()).Append(']');
        if (!Kinds.TryGetValue(kind, out var bare) || !title.Equals(bare, StringComparison.Ordinal))
            into.Append(' ').Append(title);
        if (end - at > 1)
            into.Append('\n').Append(AsideExtension.Untagged(blocks, at + 1, end - at - 1, siblings));

        consumed = end - at;
        return true;
    }

    /// <summary>The title line as a quote block: its own Markdown, so a link or a code
    /// span in a title survives the trip, wearing the callout's bold. A title that isn't
    /// a paragraph (one that opens with a <c>#</c>, say) is taken as the plain text it
    /// looks like — the marker already decided what this line is.</summary>
    private static Block TitleBlock(string title, IReadOnlyList<MarkdownExtension> siblings)
    {
        var block = new Block { Kind = BlockKind.Quote };
        var parsed = MarkdownSerializer.Parse(title, siblings);
        if (parsed is [{ Kind: BlockKind.Paragraph } line, ..])
            block.Runs.AddRange(line.Runs.Select(r => r with { Style = r.Style.With(InlineFlags.Bold) }));
        else
            block.Runs.Add(new StyledRun(title, new InlineStyle(InlineFlags.Bold)));
        return block;
    }

    /// <summary>The title back as source. The bold is the callout's own — every title
    /// wears it — so it comes off before writing; bold a writer put <em>inside</em> a
    /// title is absorbed by it, which is the one thing this line does not round-trip.</summary>
    private static string TitleSource(Block title, IReadOnlyList<MarkdownExtension> siblings)
    {
        var line = new Block { Kind = BlockKind.Paragraph };
        line.Runs.AddRange(title.Runs
            .Select(r => r with { Style = r.Style.Without(InlineFlags.Bold) })
            .Where(r => r.Length > 0));
        var doc = new RichDocument();
        doc.Load([line]);
        return MarkdownSerializer.ToMarkdown(doc, siblings).Trim();
    }

    /// <summary><c>&gt; [!WARNING] optional title</c> — the kind and the title the
    /// callout will wear, which is the kind's own name when the marker stands alone.</summary>
    private static bool TryReadMarker(string line, out string kind, out string title)
    {
        kind = "";
        title = "";
        var text = line.TrimStart();
        if (!text.StartsWith('>'))
            return false;
        text = text[1..].TrimStart();
        if (!text.StartsWith("[!", StringComparison.Ordinal))
            return false;
        var close = text.IndexOf(']');
        if (close < 0)
            return false;
        var word = text[2..close].Trim();
        if (!Kinds.TryGetValue(word, out var name))
            return false;
        kind = word.ToLowerInvariant();
        var rest = text[(close + 1)..].Trim();
        title = rest.Length > 0 ? rest : name;
        return true;
    }
}
