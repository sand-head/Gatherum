using SlopEdit.Core.Rich;

namespace Gatherum.Client;

/// <summary>Where a link inside a document goes. Reading and editing route the same
/// four cases — a wiki link by title, a mention or embedded file by id, a foreign
/// scheme out of the app, and a title nobody has written yet — so the decision lives
/// here rather than once per surface. What each surface does with the answer differs:
/// the editor has a save to flush first, the reader simply goes.</summary>
public static class LinkRouter
{
    /// <summary>Named LinkKind rather than Kind because <see cref="Target"/>'s own
    /// positional property is called Kind, and would shadow the type inside it.</summary>
    public enum LinkKind { Nowhere, Node, External, UnwrittenTitle }

    public readonly record struct Target(LinkKind Kind, string Url, string Title);

    /// <summary>Throws whatever <see cref="IAppData.ResolveTitlesAsync"/> throws: a
    /// wiki link cannot be routed without asking the server which titles exist, and
    /// only the caller knows what to say when the server is unreachable.</summary>
    public static async Task<Target> ResolveAsync(IAppData data, string url)
    {
        if (WikiLinkExtension.TargetOf(url) is { Length: > 0 } title)
        {
            var resolved = await data.ResolveTitlesAsync([title]);
            return resolved.TryGetValue(title, out var byTitle)
                ? new Target(LinkKind.Node, $"/nodes/{byTitle}", "")
                : new Target(LinkKind.UnwrittenTitle, "", title);
        }
        if (NodeUrl.TryParse(url, out var nodeId))
            return new Target(LinkKind.Node, $"/nodes/{nodeId}", "");
        if (NodeUrl.IsExternal(url))
            return new Target(LinkKind.External, url, "");
        return new Target(LinkKind.Nowhere, "", "");
    }
}
