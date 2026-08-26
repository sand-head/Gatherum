using System.Text;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;

namespace Gatherum.Core.Services;

/// <summary>Turning titles into paths and back. Two rules carry most of it:
///
/// The filename is the title — <c>Homelab/Podman.md</c> is a page called "Podman" —
/// which is what lets a directory nobody prepared read as a wiki.
///
/// A node with children needs somewhere to put them, and a file cannot contain files, so
/// children live in a sibling directory named for the node without its extension:
/// <c>Podman.md</c> beside <c>Podman/</c>. This is the one place the node tree and the
/// directory tree are not literally the same shape, and it is the same trick every
/// filesystem-backed wiki settles on.</summary>
public static class NodePaths
{
    /// <summary>Characters a path segment cannot carry, plus the ones that would make a
    /// name ambiguous to read back. A title that needs any of them keeps its bytes where
    /// they are and records an override instead.</summary>
    private static readonly char[] Illegal =
        ['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0'];

    /// <summary>Names Windows refuses outright, kept out of the way even on Linux so a
    /// store stays portable.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxSegmentBytes = 200;

    /// <summary>The directory a node's children live in: its own path without the
    /// extension. A node that is only a place in the tree is its own directory.</summary>
    public static string ChildDirectory(Node node) =>
        node.RelativePath.Length == 0
            ? ""
            : StripExtension(node.RelativePath);

    /// <summary>Whether a title can be spelled as a filename at all. When it cannot, the
    /// caller keeps the file's name and writes a title override — which is the entire
    /// reason overrides exist.</summary>
    public static bool IsLegalSegment(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(Illegal) >= 0)
            return false;
        if (name is "." or "..")
            return false;
        if (name.StartsWith(' ') || name.EndsWith(' ') || name.EndsWith('.'))
            return false;
        if (Reserved.Contains(StripExtension(name)))
            return false;
        if (name.Any(char.IsControl))
            return false;
        return Encoding.UTF8.GetByteCount(name) <= MaxSegmentBytes;
    }

    /// <summary>A filename for a title that can have one, or null when the title has to
    /// live in metadata instead.</summary>
    public static string? FileNameFor(string title, string extension)
    {
        var trimmed = title.Trim();
        var candidate = trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + extension;
        return IsLegalSegment(candidate) ? candidate : null;
    }

    /// <summary>The nearest filename for a title that cannot be one verbatim: illegal
    /// and control characters give way to spaces, whitespace folds to single spaces,
    /// and what overflows the byte budget is cut at a character boundary — or null when
    /// nothing spellable remains. The exact title is not lost to the respelling; it
    /// stays on the node as the override, the same deal every unspellable title gets.</summary>
    public static string? NearestFileNameFor(string title, string extension)
    {
        var spelled = new StringBuilder(title.Length);
        foreach (var c in title)
            spelled.Append(
                Illegal.Contains(c) || char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        var cleaned = string.Join(' ',
            spelled.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        cleaned = CutToBudget(cleaned, MaxSegmentBytes - Encoding.UTF8.GetByteCount(extension))
            .TrimEnd(' ', '.');
        return cleaned.Length == 0 ? null : FileNameFor(cleaned, extension);
    }

    /// <summary>The longest prefix that fits a UTF-8 byte budget without splitting a
    /// character.</summary>
    private static string CutToBudget(string name, int budget)
    {
        if (Encoding.UTF8.GetByteCount(name) <= budget)
            return name;
        var bytes = 0;
        var length = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > budget)
                break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return name[..length];
    }

    /// <summary>A name that is free in this directory, suffixing " (2)", " (3)" … the way
    /// every file manager does when one is taken.</summary>
    public static string Deduplicate(string fileName, Func<string, bool> taken)
    {
        if (!taken(fileName))
            return fileName;
        var stem = StripExtension(fileName);
        var extension = fileName[stem.Length..];
        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{stem} ({n}){extension}";
            if (!taken(candidate))
                return candidate;
        }
        throw new InvalidOperationException($"No free name near '{fileName}'.");
    }

    public static string Combine(string directory, string name) =>
        directory.Length == 0 ? name : $"{directory.TrimEnd('/')}/{name}";

    /// <summary>The last extension, or "" — <c>Path.GetExtension</c> without letting a
    /// directory separator in the string change the answer.</summary>
    public static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        var slash = name.LastIndexOf('/');
        return dot > slash && dot > 0 ? name[dot..] : "";
    }

    public static string StripExtension(string name)
    {
        var extension = Extension(name);
        return extension.Length == 0 ? name : name[..^extension.Length];
    }

    /// <summary>The title a file has when nothing overrides it.</summary>
    public static string DefaultTitle(string relativePath)
    {
        var name = relativePath.Split('/')[^1];
        var stem = StripExtension(name);
        return stem.Length > 0 ? stem : name;
    }

    public static NodePath PathFor(string root, Node node) => new(root, node.RelativePath);
}
