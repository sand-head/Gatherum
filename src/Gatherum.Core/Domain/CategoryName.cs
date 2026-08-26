namespace Gatherum.Core.Domain;

/// <summary>How a category is spelled. "Home lab", " home  lab " and "HOME LAB" all name
/// the same category: the text is trimmed, its inner whitespace collapsed, and the
/// identity is the lowercase result. Case survives in the name itself, which is what the
/// category is displayed as and what its file on disk is called.
///
/// The name is the whole of the spelling rule now. A category used to be identified by a
/// slash-separated path, so this file had to decide what a path meant; a category is a
/// node today, its identity is that node's id, and the name only has to be unique enough
/// to be written down in a sidecar and typed into a link.</summary>
public static class CategoryName
{
    public const int MaxLength = 100;

    /// <summary>The name as it will be displayed and filed on disk.</summary>
    public static string Collapse(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>What two spellings are compared by.</summary>
    public static string Key(string name) => Collapse(name).ToLowerInvariant();

    public static bool Same(string left, string right) =>
        string.Equals(Key(left), Key(right), StringComparison.Ordinal);
}
