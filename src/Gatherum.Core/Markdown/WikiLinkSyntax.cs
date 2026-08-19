namespace Gatherum.Core.Markdown;

/// <summary>Reads the <c>[[Title]]</c> convention out of Markdown text — the same
/// spelling slopedit's <c>WikiLinkExtension</c> gives the editor, restated here
/// because the server has to find the links a body claims without loading an editor.
/// <c>[[Target|label]]</c> links Target; escaped pipes (<c>\|</c>, how a wiki link
/// survives a table cell) separate the same way; brackets inside decline, so
/// <c>[[a[b]]</c> is text. Code spans and fenced code are skipped: a link nobody can
/// click is not a link.</summary>
public static class WikiLinkSyntax
{
    /// <summary>The titles this text wiki-links, trimmed and de-duplicated
    /// case-insensitively — resolution decides which of them are nodes.</summary>
    public static IReadOnlySet<string> Targets(string? markdown)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (markdown is null)
            return targets;

        var inFence = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("```", StringComparison.Ordinal) ||
                line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
                continue;
            ScanLine(raw, targets);
        }
        return targets;
    }

    private static void ScanLine(string line, HashSet<string> targets)
    {
        for (var i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '\\':
                    i++;                       // an escaped character is never syntax
                    break;
                case '`':
                    i = EndOfCodeSpan(line, i);
                    break;
                case '[' when TryRead(line, i, out var target, out var end):
                    targets.Add(target);
                    i = end - 1;
                    break;
            }
        }
    }

    /// <summary>Past the closing backtick of a code span, or the line's end when it
    /// never closes — an unterminated span swallows the rest of the line either way.</summary>
    private static int EndOfCodeSpan(string line, int start)
    {
        var close = line.IndexOf('`', start + 1);
        return close < 0 ? line.Length : close;
    }

    /// <summary>The link at <paramref name="at"/>, mirroring the editor's reader:
    /// <c>[[x]]</c> at minimum, no brackets inside, a pipe (escaped or not) splits
    /// target from label, and both halves must survive trimming non-empty.</summary>
    private static bool TryRead(string line, int at, out string target, out int end)
    {
        target = "";
        end = at;
        if (line.Length - at < 5 || line[at + 1] != '[')
            return false;
        var close = line.IndexOf("]]", at + 2, StringComparison.Ordinal);
        if (close < 0)
            return false;
        var inner = line[(at + 2)..close].Replace("\\|", "|");
        if (inner.Contains('[') || inner.Contains(']'))
            return false;
        var pipe = inner.IndexOf('|');
        var name = (pipe >= 0 ? inner[..pipe] : inner).Trim();
        var label = (pipe >= 0 ? inner[(pipe + 1)..] : inner).Trim();
        if (name.Length == 0 || label.Length == 0)
            return false;
        target = name;
        end = close + 2;
        return true;
    }
}
