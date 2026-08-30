using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>
/// Gatherum's Markdown dialect, in one place: the syntaxes the editor is told about per
/// call, and the only way anything in the app turns a page into a document or back.
/// slopedit ships no opinion about <c>[[wiki links]]</c>, infoboxes or callouts — it
/// edits code as often as prose — so the conventions live with the wiki that wants
/// them, and ride along on every read and every write. The set is the same on both
/// sides by construction, which is what makes the round trip lossless: a page saved
/// through here is byte-identical to the page that was read, minus the edits.
/// </summary>
public static class GatherumMarkdown
{
    public static readonly IReadOnlyList<MarkdownExtension> Extensions =
    [
        new WikiLinkExtension(),
        new AsideExtension(),
        new CalloutExtension(),
        new CollectionExtension(),
    ];

    /// <summary>A page as an editable document, dressed for the current mode.</summary>
    public static RichDocument Parse(string markdown, bool isDark)
    {
        var document = MarkdownSerializer.FromMarkdown(markdown, extensions: Extensions);
        Dress(document, isDark);
        return document;
    }

    public static string ToMarkdown(RichDocument document) =>
        MarkdownSerializer.ToMarkdown(document, Extensions);

    /// <summary>The Markdown for a slice of a document — what an insertion writes
    /// around, since a construct only exists in source and has to be read back in.</summary>
    public static string ToMarkdown(RichDocument document, int firstBlock, int count)
    {
        if (count <= 0)
            return "";
        var slice = new RichDocument();
        slice.Load(document.Blocks.Skip(firstBlock).Take(count).Select(b => b.Clone()));
        return ToMarkdown(slice);
    }

    /// <summary>Re-read a page into the document that is already open, rather than
    /// swapping in a new one: the view is bound to this instance, and so is every
    /// caret, selection and event subscription it holds. Undo does not survive — Load
    /// is a file being opened, not an edit.</summary>
    public static void Reload(RichDocument document, string markdown, bool isDark)
    {
        document.Load(MarkdownSerializer.Parse(markdown, Extensions));
        Dress(document, isDark);
        document.InvalidateLayout();
    }

    /// <summary>The ink and the chrome a document wears — everything about it that is
    /// the app's word rather than the file's, and so has to be re-said when the mode
    /// changes or the blocks move.</summary>
    public static void Dress(RichDocument document, bool isDark)
    {
        // The faces the themes name have to exist before the first measure.
        DocumentFonts.EnsureRegistered();
        // The encyclopedia's dress: the hairline under h1 and h2 and the breath
        // around every section title. Presentation, so it is said here rather than
        // stored in the file — and worn by both renderers, canvas and HTML alike,
        // because the layout is what spends it.
        document.HeadingRuleLevels = 2;
        document.HeadingSpacing = 1.5f;
        // Every heading folds its section away, in the same chevron the read view's
        // mobile fold wears: slopedit 2.6.0 made the canvas disclosure Minerva's —
        // one glyph, one size, one direction convention (down says "expand"), and
        // the heading's hairline running under it — so the editor and the article
        // now fold alike rather than in two styles. View state only: no Version, no
        // serialization, no collab op, and the caret entering a hidden region
        // unfolds it, so content is never unreachable. The HTML renderers ignore
        // the flag, so dressing a read-only document with it costs nothing.
        document.FoldableHeadings = true;
        EditorThemes.ApplyInk(document, isDark);
        DocumentChrome.Apply(document, isDark);
    }
}
