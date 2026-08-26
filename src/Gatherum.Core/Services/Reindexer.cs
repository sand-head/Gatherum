using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gatherum.Core.Services;

/// <summary>Rebuilds the index from what is on disk. This is the whole of Gatherum's
/// disaster recovery and the whole of its external-change story, because they are the
/// same operation: read the directories, and make the database agree with them.
///
/// The disk always wins. Where the index and the filesystem disagree the index yields,
/// and nothing outside <c>.gatherum</c> is ever written or deleted to resolve a
/// discrepancy — a file that turned up unexpectedly is a new node, not a mistake to
/// correct.</summary>
public class Reindexer(
    GatherumDbContext db,
    IFileStorage storage,
    INodeMetadataStore metadata,
    UserRoots roots,
    NodeService nodes,
    AccessService access,
    CategoryService categories,
    IEnumerable<ITextExtractor> extractors,
    TimeProvider clock,
    ILogger<Reindexer> logger)
{
    /// <summary>What a directory is, when it is only a place to keep things. A folder
    /// somebody made in their file manager is a node too.</summary>
    public const string DirectoryMediaType = "inode/directory";

    public async Task<ReindexReport> RunAsync(CancellationToken ct = default)
    {
        var report = new ReindexReport();
        var seen = new HashSet<Guid>();

        foreach (var root in storage.Roots())
        {
            if (await roots.OwnerOfAsync(root, ct) is not { } ownerId)
            {
                // A directory nobody signs in as is left entirely alone rather than
                // adopted: ownership is the path, and guessing an owner would be
                // inventing one.
                logger.LogInformation("Skipping {Root}: no user owns that directory.", root);
                report.SkippedRoots.Add(root);
                continue;
            }

            var files = storage.Walk(root).OrderBy(p => p.Relative, StringComparer.Ordinal).ToList();
            var byDirectory = await EnsureDirectoriesAsync(root, ownerId, files, seen, report, ct);

            foreach (var path in files)
            {
                var node = await IndexFileAsync(root, ownerId, path, byDirectory, report, ct);
                seen.Add(node.Id);
            }
        }

        // Anything the index still claims that no root produced is gone from disk, and
        // the disk is the system of record.
        var stale = await db.Nodes.Where(n => !seen.Contains(n.Id)).ToListAsync(ct);
        if (stale.Count > 0)
        {
            db.Nodes.RemoveRange(stale);
            report.Removed = stale.Count;
        }

        await db.SaveChangesAsync(ct);
        await access.RecomputeAsync(ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Reindex complete: {Added} added, {Updated} updated, {Removed} removed.",
            report.Added, report.Updated, report.Removed);
        return report;
    }

    /// <summary>Every directory that holds something becomes a node, so the tree the user
    /// sees is the tree on disk. A directory that answers to a file — <c>Podman/</c> beside
    /// <c>Podman.md</c> — is that file's node rather than one of its own.</summary>
    private async Task<Dictionary<string, Node>> EnsureDirectoriesAsync(string root, Guid ownerId,
        List<NodePath> files, HashSet<Guid> seen, ReindexReport report, CancellationToken ct)
    {
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var segments = file.Relative.Split('/');
            for (var i = 1; i < segments.Length; i++)
                wanted.Add(string.Join('/', segments[..i]));
        }

        // A directory that shares a stem with a file beside it belongs to that file.
        var fileStems = files.Select(f => NodePaths.StripExtension(f.Relative))
            .ToHashSet(StringComparer.Ordinal);

        var byDirectory = new Dictionary<string, Node>(StringComparer.Ordinal) { [""] = null! };
        foreach (var directory in wanted)
        {
            if (fileStems.Contains(directory))
                continue;

            var existing = await db.Nodes
                .FirstOrDefaultAsync(n => n.OwnerId == ownerId && n.RelativePath == directory, ct);
            if (existing is null)
            {
                existing = new Node
                {
                    Id = Guid.NewGuid(),
                    Title = NodePaths.DefaultTitle(directory),
                    MediaType = DirectoryMediaType,
                    OwnerId = ownerId,
                    RelativePath = directory,
                    CreatedAt = clock.GetUtcNow(),
                    UpdatedAt = clock.GetUtcNow(),
                };
                db.Nodes.Add(existing);
                report.Added++;
            }
            byDirectory[directory] = existing;
            seen.Add(existing.Id);
        }

        // Parent every directory node under the one above it, once they all exist.
        foreach (var (directory, node) in byDirectory)
        {
            if (directory.Length == 0 || node is null)
                continue;
            node.ParentId = ParentOf(directory, byDirectory, fileStems, ownerId)?.Id;
        }
        await db.SaveChangesAsync(ct);
        return byDirectory;
    }

    private async Task<Node> IndexFileAsync(string root, Guid ownerId, NodePath path,
        Dictionary<string, Node> byDirectory, ReindexReport report, CancellationToken ct)
    {
        var facts = await storage.MeasureAsync(path, ct);
        var sidecar = await metadata.ReadAsync(path, ct);
        var mediaType = MediaTypes.Resolve(null, path.Name);

        var node = sidecar is not null
            ? await db.Nodes.Include(n => n.File!).ThenInclude(f => f.Versions)
                .FirstOrDefaultAsync(n => n.Id == sidecar.Id, ct)
            : null;
        node ??= await db.Nodes.Include(n => n.File!).ThenInclude(f => f.Versions)
            .FirstOrDefaultAsync(n => n.OwnerId == ownerId && n.RelativePath == path.Relative, ct);

        var now = clock.GetUtcNow();
        if (node is null)
        {
            node = new Node
            {
                Id = sidecar?.Id ?? Guid.NewGuid(),
                Title = sidecar?.Title ?? path.DefaultTitle,
                MediaType = mediaType,
                OwnerId = ownerId,
                RelativePath = path.Relative,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Nodes.Add(node);
            report.Added++;
        }
        else
        {
            // The file may have moved since the index last looked; its id followed it.
            if (node.RelativePath != path.Relative || node.OwnerId != ownerId)
                report.Moved++;
            node.RelativePath = path.Relative;
            node.OwnerId = ownerId;
            node.Title = sidecar?.Title ?? path.DefaultTitle;
            node.MediaType = mediaType;
        }

        node.ParentId = DirectoryOf(path.Relative, byDirectory, ownerId)?.Id;
        node.Access = sidecar?.Access ?? AccessMode.Private;
        node.InheritAccess = sidecar?.Inherit ?? true;

        await SyncVersionsAsync(node, path, facts, sidecar, mediaType, report, ct);
        await SyncGrantsAsync(node, sidecar, ct);
        await db.SaveChangesAsync(ct);
        await SyncCategoriesAsync(node, sidecar, ownerId, ct);
        return node;
    }

    private async Task SyncVersionsAsync(Node node, NodePath path, StoredBlob facts,
        NodeMetadata? sidecar, string mediaType, ReindexReport report, CancellationToken ct)
    {
        node.File ??= new FileBody { NodeId = node.Id };
        node.File.Description = sidecar?.Description ?? node.File.Description;
        node.File.SourceUrl = sidecar?.SourceUrl ?? node.File.SourceUrl;

        var history = sidecar?.History ?? [];
        foreach (var recorded in history)
        {
            if (node.File.Versions.Any(v => v.Number == recorded.Number))
                continue;
            var version = new FileVersion
            {
                Id = Guid.NewGuid(),
                NodeId = node.Id,
                Number = recorded.Number,
                Hash = recorded.Hash,
                MediaType = recorded.MediaType,
                FileName = recorded.FileName,
                SizeBytes = recorded.SizeBytes,
                UploadedById = node.OwnerId,
                UploadedAt = recorded.UploadedAt,
            };
            node.File.Versions.Add(version);
            db.FileVersions.Add(version);
        }

        var head = node.File.Versions.Count == 0 ? null : node.File.Current;
        if (head is not null && head.Hash == facts.Hash && head.FileName == path.Name)
            return;

        // The file on disk is not what the index last saw. It becomes a new version
        // rather than a correction of the old one: the disk wins, and nothing that was
        // recorded is thrown away to make room for it.
        var text = await ExtractAsync(path, mediaType, ct);
        var fresh = new FileVersion
        {
            Id = Guid.NewGuid(),
            NodeId = node.Id,
            Number = (head?.Number ?? 0) + 1,
            Hash = facts.Hash,
            MediaType = mediaType,
            FileName = path.Name,
            SizeBytes = facts.SizeBytes,
            ExtractedText = text,
            UploadedById = node.OwnerId,
            UploadedAt = clock.GetUtcNow(),
        };
        node.File.Versions.Add(fresh);
        db.FileVersions.Add(fresh);
        if (head is not null)
            report.Updated++;
        nodes.RefreshSearchText(node);
    }

    private async Task SyncGrantsAsync(Node node, NodeMetadata? sidecar, CancellationToken ct)
    {
        var existing = await db.NodeGrants.Where(g => g.NodeId == node.Id).ToListAsync(ct);
        db.NodeGrants.RemoveRange(existing);
        foreach (var grant in sidecar?.Grants ?? [])
        {
            if (await roots.OwnerOfAsync(grant.Root, ct) is not { } granteeId
                || granteeId == node.OwnerId)
                continue;
            db.NodeGrants.Add(new NodeGrant
            {
                NodeId = node.Id,
                UserId = granteeId,
                Role = grant.Role,
            });
        }
    }

    private async Task SyncCategoriesAsync(Node node, NodeMetadata? sidecar, Guid ownerId,
        CancellationToken ct)
    {
        foreach (var path in sidecar?.Categories ?? [])
        {
            try
            {
                await categories.AddAsync(ownerId, node.Id, path, ct);
            }
            catch (ValidationException ex)
            {
                logger.LogWarning("Ignoring category '{Path}' on {Node}: {Reason}",
                    path, node.Id, ex.Message);
            }
        }
    }

    private async Task<string> ExtractAsync(NodePath path, string mediaType, CancellationToken ct)
    {
        var extractor = extractors.FirstOrDefault(e => e.CanExtract(mediaType, path.Name));
        if (extractor is null)
            return "";
        try
        {
            await using var stream = await storage.OpenReadAsync(path, ct);
            return await extractor.ExtractAsync(stream, mediaType, path.Name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Text extraction failed for {Path}", path);
            return "";
        }
    }

    private Node? DirectoryOf(string relative, Dictionary<string, Node> byDirectory, Guid ownerId)
    {
        var slash = relative.LastIndexOf('/');
        var directory = slash < 0 ? "" : relative[..slash];
        return Lookup(directory, byDirectory, ownerId);
    }

    private Node? ParentOf(string directory, Dictionary<string, Node> byDirectory,
        HashSet<string> fileStems, Guid ownerId)
    {
        var slash = directory.LastIndexOf('/');
        return Lookup(slash < 0 ? "" : directory[..slash], byDirectory, ownerId);
    }

    /// <summary>The node a directory belongs to: the directory's own node, or the file it
    /// shares a stem with — <c>Podman/</c> hangs off <c>Podman.md</c>.</summary>
    private Node? Lookup(string directory, Dictionary<string, Node> byDirectory, Guid ownerId)
    {
        if (directory.Length == 0)
            return null;
        if (byDirectory.TryGetValue(directory, out var known) && known is not null)
            return known;
        return db.Nodes.Local.FirstOrDefault(n => n.OwnerId == ownerId
                && NodePaths.StripExtension(n.RelativePath) == directory)
            ?? db.Nodes.FirstOrDefault(n => n.OwnerId == ownerId
                && n.RelativePath.StartsWith(directory));
    }
}

public class ReindexReport
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Moved { get; set; }
    public int Removed { get; set; }
    public List<string> SkippedRoots { get; } = [];
}
