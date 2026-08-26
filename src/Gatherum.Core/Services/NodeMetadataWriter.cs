using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>Puts a node's metadata back on disk after anything changes it. One writer,
/// called from every mutation, rather than each service spelling out its own slice: a
/// sidecar that is only sometimes current is worse than none, because the reindex would
/// believe it.</summary>
public class NodeMetadataWriter(GatherumDbContext db, INodeMetadataStore store, UserRoots roots)
{
    public async Task WriteAsync(Guid nodeId, CancellationToken ct = default)
    {
        var node = await db.Nodes
            .Include(n => n.File!).ThenInclude(f => f.Versions)
            .Include(n => n.Categories).ThenInclude(c => c.Category)
            .Include(n => n.Grants)
            .FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || node.RelativePath.Length == 0)
            return;

        var root = await roots.ForAsync(node.OwnerId, ct);
        var path = new NodePath(root, node.RelativePath);

        var grants = new List<MetadataGrant>();
        foreach (var grant in node.Grants)
        {
            var granteeRoot = await db.Users.Where(u => u.Id == grant.UserId)
                .Select(u => u.RootName).FirstOrDefaultAsync(ct);
            if (granteeRoot is not null)
                grants.Add(new MetadataGrant(granteeRoot, grant.Role));
        }

        var history = new List<MetadataVersion>();
        if (node.File is { Versions.Count: > 0 } body)
        {
            var head = body.Current.Number;
            foreach (var version in body.Versions.Where(v => v.Number != head)
                .OrderBy(v => v.Number))
            {
                var uploader = await db.Users.Where(u => u.Id == version.UploadedById)
                    .Select(u => u.RootName).FirstOrDefaultAsync(ct) ?? root;
                history.Add(new MetadataVersion(version.Number, version.Hash, version.FileName,
                    version.MediaType, version.SizeBytes, uploader, version.UploadedAt));
            }
        }

        await store.WriteAsync(path, new NodeMetadata
        {
            Id = node.Id,
            // Only an override is worth recording. A title that matches the filename is
            // already on disk, and writing it down again would be a second copy to keep
            // true — and the one that eventually disagrees.
            Title = node.Title == NodePaths.DefaultTitle(node.RelativePath) ? null : node.Title,
            Description = node.File?.Description ?? "",
            SourceUrl = node.File is { SourceUrl.Length: > 0 } bookmark ? bookmark.SourceUrl : null,
            Category = node.IsCategory,
            Categories = node.Categories.Select(c => c.Category!.Title)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList(),
            Access = node.Access,
            Inherit = node.InheritAccess,
            Grants = grants,
            History = history,
        }, ct);
    }
}
