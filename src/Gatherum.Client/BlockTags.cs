namespace Gatherum.Client;

/// <summary>
/// What Gatherum writes into a block's <see cref="SlopEdit.Core.Rich.Block.Tag"/>: which
/// construct — and which <em>instance</em> of it — a block belongs to. The tag is the
/// construct's Markdown source spelled as it was written (an aside's fence arguments,
/// <c>infobox</c> or <c>figure left 320</c>; a callout's kind, <c>callout warning</c>)
/// plus a <c>#n</c> marker that makes one instance distinguishable from the next.
///
/// The marker earns its keep at the seams: a run of blocks is "the same construct" while
/// the tag is equal, so without it two adjacent infoboxes — or two warnings in a row —
/// would read as one card and be written back as one fence, silently eating the second
/// one's opening line. Blocks the editor made are untagged and stay ordinary; a split
/// (Enter inside a construct) inherits the whole tag, marker included, so editing inside
/// one stays inside it.
/// </summary>
public static class BlockTags
{
    public const string Infobox = "infobox";
    public const string Figure = "figure";
    public const string Callout = "callout";
    /// <summary>The word a shared list opens with when nothing says otherwise; the rest
    /// are <see cref="ListVocabulary.All"/>'s keys.</summary>
    public const string Collection = "collection";

    private static int instances;

    /// <summary>A tag for one instance of a construct: its source words, then the
    /// marker that separates it from an identical neighbour.</summary>
    public static string For(string source) =>
        $"{source} #{Interlocked.Increment(ref instances)}";

    /// <summary>The construct word of a tag, or null when a block wears none.</summary>
    public static string? KindOf(string? tag) =>
        string.IsNullOrEmpty(tag) ? null : tag.Split(' ', 2)[0];

    /// <summary>The tag without its instance marker — the construct's own source words,
    /// which is what a writer puts back in the file.</summary>
    public static string SourceOf(string? tag) =>
        string.IsNullOrEmpty(tag) ? "" : string.Join(' ', Words(tag));

    /// <summary>Whatever follows the construct word — a side, a width, a callout's
    /// kind — with the instance marker left out.</summary>
    public static string[] ArgumentsOf(string? tag) =>
        string.IsNullOrEmpty(tag) ? [] : Words(tag)[1..];

    /// <summary>Whether the tag belongs to <see cref="AsideExtension"/> — the runs that
    /// leave the vertical flow.</summary>
    public static bool IsAside(string? tag) => KindOf(tag) is Infobox or Figure;

    /// <summary>Whether the tag belongs to <see cref="CalloutExtension"/>.</summary>
    public static bool IsCallout(string? tag) => KindOf(tag) == Callout;

    /// <summary>Whether the tag belongs to <see cref="CollectionExtension"/> — the one
    /// construct the reading view renders as a component rather than as prose. Several
    /// words open one; they differ only in what the reading view calls things.</summary>
    public static bool IsCollection(string? tag) =>
        KindOf(tag) is { } kind && ListVocabulary.All.ContainsKey(kind);

    /// <summary>What a collection fence named, from the tag its blocks wear: a list, a
    /// <c>[[title]]</c> or a mention, spelled the way the source spelled it.</summary>
    public static string ArgumentOf(string? tag) => string.Join(' ', ArgumentsOf(tag));

    private static string[] Words(string tag) =>
    [
        .. tag.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.StartsWith('#')),
    ];
}
