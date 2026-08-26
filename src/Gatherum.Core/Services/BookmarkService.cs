using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;

namespace Gatherum.Core.Services;

/// <summary>Bookmarks: a URL captured as a file node, the way an archive keeps a page
/// rather than the way a browser keeps a link. The capture is the node's content — an
/// HTML snapshot, or the document itself when the URL serves one — so a bookmark is
/// searchable, versioned, categorized and shared like anything else in the tree, and
/// capturing again is just a new version. Nothing here is periodic: a page is fetched
/// when a person (or their agent) asks, and never again until they ask again.</summary>
public class BookmarkService(
    GatherumDbContext db,
    NodeService nodes,
    FileService files,
    IPageArchiver archiver,
    NodeMetadataWriter sidecar)
{
    public async Task<Node> SaveAsync(Guid userId, Guid? parentId, string url,
        CancellationToken ct = default)
    {
        var uri = Validate(url);
        var page = await CaptureAsync(uri, ct);
        using var content = new MemoryStream(page.Content);
        var node = await files.CreateFileNodeAsync(userId, parentId, page.FileName,
            page.MediaType, content, page.Title, ct);
        node.File!.SourceUrl = uri.AbsoluteUri;
        nodes.RefreshSearchText(node);
        await db.SaveChangesAsync(ct);
        await sidecar.WriteAsync(node.Id, ct);
        return node;
    }

    /// <summary>Fetches a bookmark's URL again and keeps what came back as a new
    /// version, so a page's history accrues the way an archive's does — the old capture
    /// stays readable after the page moves on, or goes away.</summary>
    public async Task<Node> CaptureAgainAsync(Guid userId, Guid nodeId,
        CancellationToken ct = default)
    {
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        if (node.File is not { SourceUrl.Length: > 0 } file)
            throw new ValidationException(
                $"Node {nodeId} is not a bookmark — it has no source URL to fetch.");
        var page = await CaptureAsync(new Uri(file.SourceUrl), ct);
        using var content = new MemoryStream(page.Content);
        return await files.UploadVersionAsync(userId, nodeId, page.FileName, page.MediaType,
            content, ct);
    }

    private static Uri Validate(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new ValidationException("A bookmark needs an absolute http(s) URL.");
        return uri;
    }

    /// <summary>A failed fetch surfaces as a validation error: the URL is the input, and
    /// "that server answered 404" is a sentence about it that the person who pasted it
    /// can act on.</summary>
    private async Task<ArchivedPage> CaptureAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            return await archiver.ArchiveAsync(uri, ct);
        }
        catch (PageArchiveException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }
}
