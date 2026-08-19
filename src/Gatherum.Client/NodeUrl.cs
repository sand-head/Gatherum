namespace Gatherum.Client;

/// <summary>The two URL shapes that mean "a node in this Gatherum" — a mention's
/// <c>node://id</c> and an embedded file's <c>/api/files/id/content</c> — recognized
/// here because the interactive components' only view of the world is
/// <see cref="IAppData"/>: they run in WebAssembly, where the server's
/// <c>MarkdownContent</c> isn't. The conventions themselves are Core's; this is the
/// reader for them.</summary>
public static class NodeUrl
{
    private const string Scheme = "node://";

    public static bool TryParse(string? url, out Guid nodeId)
    {
        nodeId = Guid.Empty;
        if (url is null)
            return false;
        if (url.StartsWith(Scheme, StringComparison.Ordinal))
            return Guid.TryParse(url[Scheme.Length..], out nodeId);
        if (!url.StartsWith("/api/files/", StringComparison.Ordinal))
            return false;
        var rest = url["/api/files/".Length..];
        var slash = rest.IndexOf('/');
        return slash > 0 && Guid.TryParse(rest[..slash], out nodeId);
    }

    /// <summary>Whether a URL leaves the app — anything with a scheme that isn't ours,
    /// which the editor hands to the browser rather than the router.</summary>
    public static bool IsExternal(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute) &&
        absolute.Scheme is "http" or "https" or "mailto";
}
