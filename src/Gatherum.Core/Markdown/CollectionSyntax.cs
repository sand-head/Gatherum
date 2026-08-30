using System.Text;
using System.Text.RegularExpressions;

namespace Gatherum.Core.Markdown;

/// <summary>
/// Reads and writes the <c>:::collection</c> fence — the one construct a collaborative
/// list needs, restated on the server for the same reason
/// <see cref="WikiLinkSyntax"/> is: the aggregate has to find what a body says without
/// loading an editor. The client's <c>CollectionExtension</c> is the other half, and the
/// two have to agree about every line here.
///
/// A fence whose argument is a name <em>declares</em> a list — the catalogue, the rows
/// everyone answers. One whose argument names another node <em>tracks</em> that node's
/// list — a tally, one person's answers. Inside, the vocabulary is the dialect's own:
/// bulleted items, nested one level for variants, a task marker where a tally records a
/// tick, and an em-dashed tail that is a note rather than part of the item.
///
/// The construct is one mechanism with several words for it — the same shape
/// <c>CalloutExtension</c> has, where five spellings share one implementation. Nothing
/// under here knows what a row <em>means</em>: "who has which sprite" and "who can make
/// which night" are the same question asked of different nouns, so the word a fence opens
/// with is carried through the parse and the write and read back out again, and the only
/// thing it decides is the words the reading view puts around the grid.
/// </summary>
public static class CollectionSyntax
{
    public const string Fence = ":::";

    /// <summary>The words that open one of these. Each is a different question with the
    /// same shape, and the reading view says so — see <c>ListVocabulary</c>, the client
    /// half, which keys its chrome off exactly these. Restated on the server for the
    /// reason <see cref="WikiLinkSyntax"/> is: the aggregate has to find what a body says
    /// without loading an editor.</summary>
    public static readonly IReadOnlyList<string> Kinds =
    [
        "collection",       // what everyone has
        "availability",     // when everyone can
    ];

    /// <summary>The word a list is written with when nothing says otherwise.</summary>
    public const string Word = "collection";

    /// <summary>What separates an item from the note after it. Two spellings because
    /// one of them is typeable.</summary>
    private static readonly string[] NoteSeparators = [" — ", " -- "];

    private static readonly Regex Mention = new(
        @"^\[(?<text>[^\]]+)\]\(node://(?<id>[0-9a-fA-F-]{36})\)$", RegexOptions.Compiled);

    private static readonly Regex Item = new(
        @"^(?<indent>\s*)[-*+][ \t]+(\[(?<tick>[ xX])\][ \t]+)?(?<body>.*)$", RegexOptions.Compiled);

    /// <summary>Every collection fence in a body, in the order they appear.</summary>
    public static IReadOnlyList<CollectionBlock> Read(string? markdown)
    {
        var blocks = new List<CollectionBlock>();
        if (markdown is null)
            return blocks;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (ArgumentOf(lines[i], out var word) is not { } argument)
                continue;
            var end = i + 1;
            while (end < lines.Length && lines[end].Trim() != Fence)
                end++;
            if (end >= lines.Length)
                break;                          // unterminated: not a construct after all
            blocks.Add(new CollectionBlock(word!, argument, Tracked(argument),
                ReadItems(lines[(i + 1)..end]), i, end - i + 1));
            i = end;
        }
        return blocks;
    }

    /// <summary>The one fence a caller means, matched on its argument — or the first
    /// collection on the page when nothing is named, which is what a link to the page
    /// rather than to a list on it can mean.</summary>
    public static CollectionBlock? Find(string? markdown, string? argument)
    {
        var blocks = Read(markdown);
        if (string.IsNullOrWhiteSpace(argument))
            return blocks.FirstOrDefault();
        // Spacing is not part of a name: the editor hands back an argument respelled with
        // single spaces, and it has to find the fence the file spelled with two.
        var wanted = Words(argument);
        return blocks.FirstOrDefault(b =>
            string.Equals(Words(b.Argument), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The whole fence as source, ready to stand in a file. Items that carry no
    /// tick state — a catalogue's — are written as plain bullets, so declaring a list
    /// never puts an empty checkbox in front of every line of it.</summary>
    public static string Write(string word, string argument,
        IReadOnlyList<CollectionEntry> items, bool ticked)
    {
        var source = new StringBuilder();
        source.Append(Fence).Append(Known(word) ? word : Word);
        if (argument.Length > 0)
            source.Append(' ').Append(argument);
        source.Append('\n');
        foreach (var item in items)
            WriteItem(source, item, ticked, depth: 0);
        source.Append(Fence);
        return source.ToString();
    }

    /// <summary>A body with one fence's source swapped for another's, and every line
    /// around it left exactly as it was. Rewriting a tally is the whole write path, and
    /// it must never touch the prose somebody wrote above their list.</summary>
    public static string Replace(string markdown, CollectionBlock block, string replacement)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var rebuilt = new List<string>(lines[..block.FirstLine]);
        rebuilt.AddRange(replacement.Split('\n'));
        rebuilt.AddRange(lines[(block.FirstLine + block.LineCount)..]);
        return string.Join('\n', rebuilt);
    }

    /// <summary>Whether a catalogue's item and a tally's are the same collectible.
    /// Two linked items are the ids they carry, which is what makes a rename survivable;
    /// anything else is its text, which is what makes linking optional. The asymmetry is
    /// deliberate — it is the rule that lets an item gain a page later without the ticks
    /// already made against it stopping counting.</summary>
    public static bool Matches(CollectionEntry catalogueItem, CollectionEntry tallyItem) =>
        catalogueItem.NodeId is { } mine && tallyItem.NodeId is { } theirs
            ? mine == theirs
            : Normalize(catalogueItem.Label) == Normalize(tallyItem.Label);

    /// <summary>An item's text as a match sees it: link spellings shed, whitespace
    /// collapsed, case forgotten. <c>[[Klombo]]</c>, <c>[Klombo](node://…)</c> and
    /// <c>Klombo</c> are one collectible written three ways.</summary>
    public static string Normalize(string label)
    {
        var text = PlainText(label);
        var squeezed = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = squeezed.Length > 0;
                continue;
            }
            if (space)
                squeezed.Append(' ');
            space = false;
            squeezed.Append(char.ToLowerInvariant(c));
        }
        return squeezed.ToString();
    }

    /// <summary>A label with its link syntax taken off, for reading rather than
    /// matching: what a grid puts in the row.</summary>
    public static string PlainText(string label)
    {
        var text = Regex.Replace(label, @"\[\[([^\]|]*)\|([^\]]*)\]\]", "$2");
        text = Regex.Replace(text, @"\[\[([^\]]*)\]\]", "$1");
        text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        return text.Trim();
    }

    /// <summary>A name with its spacing normalized — the only part of an argument's
    /// spelling that is not meaningful.</summary>
    public static string Words(string argument) =>
        string.Join(' ', argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Whether a word opens one of these.</summary>
    public static bool Known(string? word) =>
        word is not null && Kinds.Contains(word, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>:::collection Override sprites</c>, <c>:::availability Game nights</c>
    /// — the word this fence opened with and the argument it carries, or null when the
    /// line opens something else (or nothing).</summary>
    private static string? ArgumentOf(string line, out string? word)
    {
        word = null;
        var text = line.Trim();
        if (!text.StartsWith(Fence, StringComparison.Ordinal))
            return null;
        var rest = text[Fence.Length..].TrimStart();
        foreach (var candidate in Kinds)
        {
            if (!rest.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                continue;
            var after = rest[candidate.Length..];
            if (after.Length > 0 && !char.IsWhiteSpace(after[0]))
                continue;                       // ":::collections" is somebody else's
            word = candidate;
            return after.Trim();
        }
        return null;
    }

    /// <summary>The node a fence's argument names, when it names one. A wiki link is a
    /// title and a mention is an id — the difference matters, because a title is a search
    /// and an id is permission, so only the second spelling can name an unlisted
    /// catalogue. Whatever follows names the list on that node.</summary>
    private static CollectionTarget? Tracked(string argument)
    {
        if (argument.StartsWith("[[", StringComparison.Ordinal))
        {
            var close = argument.IndexOf("]]", 2, StringComparison.Ordinal);
            if (close < 0)
                return null;
            var inner = argument[2..close].Replace("\\|", "|");
            var pipe = inner.IndexOf('|');
            var title = (pipe >= 0 ? inner[..pipe] : inner).Trim();
            return title.Length == 0
                ? null
                : new CollectionTarget(null, title, argument[(close + 2)..].Trim());
        }
        if (!argument.StartsWith('['))
            return null;
        var match = Regex.Match(argument,
            @"^\[[^\]]*\]\(node://(?<id>[0-9a-fA-F-]{36})\)");
        return match.Success && Guid.TryParse(match.Groups["id"].Value, out var id)
            ? new CollectionTarget(id, null, argument[match.Length..].Trim())
            : null;
    }

    private static IReadOnlyList<CollectionEntry> ReadItems(IReadOnlyList<string> lines)
    {
        var items = new List<CollectionEntry>();
        var variants = new List<CollectionEntry>();
        CollectionEntry? open = null;

        void Close()
        {
            if (open is not null)
                items.Add(open with { Variants = variants.ToList() });
            open = null;
            variants.Clear();
        }

        foreach (var line in lines)
        {
            var match = Item.Match(line);
            if (!match.Success)
                continue;                       // prose inside a list is not an item
            if (Entry(match) is not { Label.Length: > 0 } entry)
                continue;                       // "- [ ]" with nothing after it says nothing
            if (match.Groups["indent"].Value.Length == 0)
            {
                Close();
                open = entry;
            }
            else if (open is not null)
            {
                variants.Add(entry);
            }
        }
        Close();
        return items;
    }

    private static CollectionEntry Entry(Match match)
    {
        var body = match.Groups["body"].Value.Trim();
        var note = "";
        foreach (var separator in NoteSeparators)
        {
            var at = body.IndexOf(separator, StringComparison.Ordinal);
            if (at < 0)
                continue;
            note = body[(at + separator.Length)..].Trim();
            body = body[..at].Trim();
            break;
        }
        var mention = Mention.Match(body);
        var id = mention.Success && Guid.TryParse(mention.Groups["id"].Value, out var parsed)
            ? parsed
            : (Guid?)null;
        return new CollectionEntry(body, id, PlainText(body), note,
            match.Groups["tick"].Value is "x" or "X", []);
    }

    private static void WriteItem(StringBuilder source, CollectionEntry item, bool ticked,
        int depth)
    {
        source.Append(new string(' ', depth * 2)).Append("- ");
        if (ticked)
            source.Append(item.Checked ? "[x] " : "[ ] ");
        source.Append(item.Label);
        if (item.Note.Length > 0)
            source.Append(" — ").Append(item.Note);
        source.Append('\n');
        foreach (var variant in item.Variants)
            WriteItem(source, variant, ticked, depth + 1);
    }
}

/// <summary>One <c>:::collection</c> fence as it stands in a file: what it said, what it
/// holds, and where it is, so a write can put another one exactly there.</summary>
public sealed record CollectionBlock(
    string Word,
    string Argument,
    CollectionTarget? Tracks,
    IReadOnlyList<CollectionEntry> Items,
    int FirstLine,
    int LineCount)
{
    /// <summary>Whether this fence declares a list of its own rather than tracking
    /// somebody else's.</summary>
    public bool Declares => Tracks is null;

    /// <summary>What this list is called: its own name where it declares one, and the
    /// name it tracks on the other node where it does not.</summary>
    public string Name => Tracks?.List ?? Argument;
}

/// <summary>The catalogue a tally follows: named by title (a search, so it cannot reach
/// an unlisted page) or by id (permission, so it can), and optionally which list on it.</summary>
public sealed record CollectionTarget(Guid? NodeId, string? Title, string List);

/// <summary>One line of a collection: what it says, the page it links if it has one, the
/// note after it, whether this file ticks it, and the variants nested under it.</summary>
public sealed record CollectionEntry(
    string Label,
    Guid? NodeId,
    string Text,
    string Note,
    bool Checked,
    IReadOnlyList<CollectionEntry> Variants)
{
    /// <summary>How many collectibles this line stands for — itself, or its variants
    /// where it has any. Every count in an interface has to be of these rather than of
    /// lines, or the progress it reports is fiction.</summary>
    public int Collectibles => Variants.Count > 0 ? Variants.Count : 1;
}
