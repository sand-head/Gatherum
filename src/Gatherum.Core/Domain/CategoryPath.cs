namespace Gatherum.Core.Domain;

/// <summary>How a category path is spelled. "Homelab / Podman", "homelab/podman" and
/// " HOMELAB/Podman " all name the same category: segments are trimmed, their inner
/// whitespace collapsed, and the identity is the lowercase join. Case survives only in
/// the segments themselves, which is what a category is displayed as.</summary>
public static class CategoryPath
{
    public const char Separator = '/';
    public const int MaxSegmentLength = 100;
    public const int MaxDepth = 8;

    /// <summary>The segments a written path names, in their writer's capitalization.</summary>
    public static List<string> Segments(string path) => path
        .Split(Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Collapse)
        .Where(segment => segment.Length > 0)
        .ToList();

    /// <summary>The identity of a written path — empty when the text names no category.</summary>
    public static string Normalize(string path) => Join(Segments(path));

    public static string Join(IEnumerable<string> segments) =>
        string.Join(Separator, segments.Select(segment => segment.ToLowerInvariant()));

    /// <summary>Every path from the root down to this one: a, a/b, a/b/c. A node in a
    /// category is in all of them, which is what "nested" means.</summary>
    public static IEnumerable<string> Ancestry(string path)
    {
        var chain = "";
        foreach (var segment in path.Split(Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            chain = chain.Length == 0 ? segment : $"{chain}{Separator}{segment}";
            yield return chain;
        }
    }

    /// <summary>The path of the category this one is nested in, or null at a root.</summary>
    public static string? Parent(string path)
    {
        var cut = path.LastIndexOf(Separator);
        return cut < 0 ? null : path[..cut];
    }

    public static bool IsDescendantOf(string path, string ancestor) =>
        path.StartsWith($"{ancestor}{Separator}", StringComparison.Ordinal);

    /// <summary>The words a category contributes to a node's search text: its own name
    /// and every name it is nested under, so a search for the parent finds the child's
    /// members.</summary>
    public static string Words(string path) => path.Replace(Separator, ' ');

    private static string Collapse(string segment) =>
        string.Join(' ', segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
