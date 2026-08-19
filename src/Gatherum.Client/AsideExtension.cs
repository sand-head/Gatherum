using System.Text;
using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>
/// The two things an encyclopedia sets beside its prose — an infobox and a captioned
/// figure — as a directive fence the editor never learns:
/// <code>
/// :::infobox
/// # Chapter 12
/// | Words | 4,210 |
/// :::
///
/// :::figure left 320
/// ![The rack](/api/files/…/content)
/// The homelab, before the rewire
/// :::
/// </code>
/// The fence's argument line becomes each block's <see cref="Block.Tag"/>, which is
/// both how the writer finds the run again and how <see cref="DocumentChrome"/> knows
/// what to float and what to paint — the geometry is derived from the tag on every
/// edit rather than pinned at parse, because block indices move as a page is written.
/// Inside the fence the body is ordinary Markdown parsed by the ordinary parser with
/// the other extensions active; a reader that has never heard of any of this shows two
/// <c>:::</c> lines and everything between them.
/// </summary>
public sealed class AsideExtension : MarkdownBlockExtension
{
    private const string Fence = ":::";

    public override bool TryRead(IReadOnlyList<string> lines, int at,
        List<Block> into, List<BlockDecoration> decorations, List<FloatedRun> floats,
        IReadOnlyList<MarkdownExtension> siblings, out int consumed)
    {
        consumed = 0;
        if (TagOf(lines[at]) is not { } tag)
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
        // Asides don't nest: the outer fence owns every block inside it, so a
        // construct within one degrades to the vocabulary it is made of.
        foreach (var b in blocks)
            b.Tag = tag;
        Style(blocks, BlockTags.KindOf(tag)!);
        into.AddRange(blocks);

        consumed = i - at + 1;                  // the fence lines too
        return true;
    }

    public override bool TryWrite(IReadOnlyList<Block> blocks, int at,
        StringBuilder into, IReadOnlyList<MarkdownExtension> siblings, out int consumed)
    {
        consumed = 0;
        var tag = blocks[at].Tag;
        if (!BlockTags.IsAside(tag))
            return false;
        var end = at;
        while (end < blocks.Count && blocks[end].Tag == tag)
            end++;

        into.Append(Fence).Append(BlockTags.SourceOf(tag)).Append('\n')
            .Append(Untagged(blocks, at, end - at, siblings)).Append('\n')
            .Append(Fence);
        consumed = end - at;
        return true;
    }

    /// <summary>A block run written through the ordinary writer: the clones shed their
    /// tag, or the writer would offer them straight back to the extension that owns it.</summary>
    internal static string Untagged(IReadOnlyList<Block> blocks, int at, int count,
        IReadOnlyList<MarkdownExtension> siblings)
    {
        var doc = new RichDocument();
        doc.Load(blocks.Skip(at).Take(count).Select(b =>
        {
            var clone = b.Clone();
            clone.Tag = null;
            return clone;
        }));
        return MarkdownSerializer.ToMarkdown(doc, siblings);
    }

    /// <summary><c>:::infobox</c>, <c>:::figure left 320</c> — the tag this fence's
    /// blocks will wear, or null when the line isn't one of ours. Side and width stay in
    /// the tag rather than being resolved here: they are the source's word, and the
    /// writer has to give it back unchanged.</summary>
    private static string? TagOf(string line)
    {
        var text = line.Trim();
        if (!text.StartsWith(Fence, StringComparison.Ordinal))
            return null;
        var parts = text[Fence.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] is not (BlockTags.Infobox or BlockTags.Figure))
            return null;
        return BlockTags.For(string.Join(' ', parts));
    }

    /// <summary>What the ordinary parser produced, dressed as the construct it is: an
    /// infobox's headings and picture center and its table sheds the grid (an infobox is
    /// a table without one); a figure centers its picture and caption. Only styling that
    /// Markdown cannot say goes here — alignment and the grid flag never serialize, so
    /// reading a page and saving it leaves the file exactly as it was. Bold labels are
    /// the source's word (<c>| **Label** | Value |</c>), not ours to add.</summary>
    private static void Style(List<Block> blocks, string kind)
    {
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case BlockKind.Heading when kind == BlockTags.Infobox:
                    block.Alignment = BlockAlignment.Center;
                    break;
                case BlockKind.Image:
                    block.Alignment = BlockAlignment.Center;
                    break;
                case BlockKind.Paragraph when kind == BlockTags.Figure:
                    block.Alignment = BlockAlignment.Center;
                    break;
                case BlockKind.TableRow when kind == BlockTags.Infobox:
                    block.TableGrid = false;
                    break;
            }
        }
    }
}
