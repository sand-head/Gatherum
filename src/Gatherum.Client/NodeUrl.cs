namespace Gatherum.Client;

/// <summary>The URL shapes that mean "a node in this Gatherum" — a mention's
/// <c>node://id</c>, an embedded file's <c>/api/files/id/content</c>, and the page's own
/// <c>/nodes/id</c> — recognized here because the interactive components' only view of
/// the world is <see cref="IAppData"/>: they run in WebAssembly, where the server's
/// <c>MarkdownContent</c> isn't. The conventions themselves are Core's; this is the
/// reader for them.</summary>
public static class NodeUrl
{
    private const string Scheme = "node://";
    private const string PagePrefix = "/nodes/";
    private const string FileContent = "/api/files/";

    /// <summary>Where a node is read. A mention is written as <c>node://id</c> and stored
    /// that way, but the read view addresses it as this — see
    /// <see cref="NodeLinks.Address"/> for why.</summary>
    public static string Page(Guid nodeId) => PagePrefix + nodeId;

    public static bool TryParse(string? url, out Guid nodeId)
    {
        nodeId = Guid.Empty;
        if (url is null)
            return false;
        if (url.StartsWith(Scheme, StringComparison.Ordinal))
            return Guid.TryParse(url[Scheme.Length..], out nodeId);
        if (url.StartsWith(PagePrefix, StringComparison.Ordinal))
            return Guid.TryParse(url[PagePrefix.Length..], out nodeId);
        if (!url.StartsWith(FileContent, StringComparison.Ordinal))
            return false;
        var rest = url[FileContent.Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 && Guid.TryParse(rest[..slash], out nodeId);
    }

    /// <summary>Whether a URL is a mention as the page stores it, which is the one shape
    /// the read view has to rewrite before a browser can be handed it.</summary>
    public static bool IsMention(string? url) =>
        url is not null && url.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>Whether a URL leaves the app — anything with a scheme that isn't ours,
    /// which the editor hands to the browser rather than the router.</summary>
    public static bool IsExternal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
        absolute.Scheme is "http" or "https" or "mailto";
}
