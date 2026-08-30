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
    public async Task<ReindexReport> RunAsync(CancellationToken ct = default)
    {
        var report = new ReindexReport();
        var seen = new HashSet<Guid>();
        var filings = new List<Filing>();

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

            var walked = storage.Walk(root).OrderBy(p => p.Relative, StringComparer.Ordinal).ToList();
            var byDirectory = await EnsureDirectoriesAsync(root, ownerId, walked, seen, report, ct);

            foreach (var path in walked)
            {
                var (node, filed) = await IndexFileAsync(root, ownerId, path, byDirectory,
                    report, ct);
                seen.Add(node.Id);
                filings.Add(filed);
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
        // The taxonomy is wired in one pass after every root, because it is the one thing
        // on disk that points at other nodes: a page filed under "Podman" cannot be joined
        // up until whichever root holds Podman's own page has been walked. Access is
        // recomputed after it rather than before, so a category page written here — one
        // only a sidecar knew about — gets its reach computed in the same run it appeared.
        await WireTaxonomyAsync(filings, report, ct);
        // Links come after the taxonomy for the same reason it came after the walk: a
        // [[wiki link]] resolves by title, so every title has to exist before any of them
        // can be looked up. Without this pass a rebuilt index has no backlinks at all
        // until somebody re-saves each page — and a shared list, which finds everyone's
        // answers by exactly these rows, would come back empty.
        report.Links = await RewireLinksAsync(ct);
        await access.RecomputeAsync(ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Reindex complete: {Added} added, {Updated} updated, {Removed} removed, " +
            "{Links} links.",
            report.Added, report.Updated, report.Removed, report.Links);
        return report;
    }

    /// <summary>Rebuilds every node's outbound links from its own bytes. The bodies are
    /// the system of record here as everywhere else: nothing is read from the old index,
    /// which is the point — this runs when there is no old index to read.
    ///
    /// Titles resolve as the node's owner, which is who resolved them when the page was
    /// written. A link the author could not have made is not one a rebuild should invent.</summary>
    private async Task<int> RewireLinksAsync(CancellationToken ct)
    {
        var bodies = await db.Nodes
            .Include(n => n.File!).ThenInclude(f => f.Versions)
            .Where(n => n.File != null && n.File.Versions.Count > 0)
            .ToListAsync(ct);
        foreach (var node in bodies)
            await nodes.RefreshLinksAsync(node, node.OwnerId, ct);
        await db.SaveChangesAsync(ct);
        return await db.NodeLinks.CountAsync(ct);
    }

    /// <summary>Every directory that holds something becomes a node, so the tree the user
    /// sees is the tree on disk. A directory that answers to a file — <c>Podman/</c> beside
    /// <c>Podman.md</c> — is that file's node rather than one of its own.</summary>
    private async Task<Dictionary<string, Node>> EnsureDirectoriesAsync(string root, Guid ownerId,
        List<NodePath> walked, HashSet<Guid> seen, ReindexReport report, CancellationToken ct)
    {
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in walked)
        {
            var segments = file.Relative.Split('/');
            for (var i = 1; i < segments.Length; i++)
                wanted.Add(string.Join('/', segments[..i]));
        }

        // A directory that shares a stem with a file beside it belongs to that file.
        var fileStems = walked.Select(f => NodePaths.StripExtension(f.Relative))
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
                    MediaType = MediaTypes.Directory,
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

    private async Task<(Node Node, Filing Filed)> IndexFileAsync(string root, Guid ownerId,
        NodePath path, Dictionary<string, Node> byDirectory, ReindexReport report,
        CancellationToken ct)
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
        node.IsCategory = sidecar?.Category ?? false;

        await SyncVersionsAsync(node, path, facts, sidecar, mediaType, report, ct);
        await SyncGrantsAsync(node, sidecar, ct);
        await db.SaveChangesAsync(ct);
        return (node, new Filing(node.Id, ownerId, sidecar?.Categories ?? []));
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
        await nodes.RefreshSearchTextAsync(node, ct);
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

    /// <summary>Rebuilds the taxonomy from what the sidecars say, once every root has been
    /// walked. The memberships are dropped and re-made rather than reconciled: the sidecar
    /// is the system of record for what a node is about, so an edge the index holds that no
    /// sidecar claims is an edge that was deleted.
    ///
    /// A name nothing on disk answers to — a category somebody typed into a <c>meta.json</c>
    /// by hand and never wrote a page for — gets its page written here. That is the one
    /// place the reindex creates a file outside <c>.gatherum</c>, and it is deliberate: a
    /// category is a page now, and a taxonomy half of which exists only in the database
    /// would not survive the next cold start.</summary>
    private async Task WireTaxonomyAsync(List<Filing> filings, ReindexReport report,
        CancellationToken ct)
    {
        var filedIds = filings.Select(f => f.NodeId).ToList();
        db.NodeCategories.RemoveRange(await db.NodeCategories
            .Where(m => filedIds.Contains(m.NodeId)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var category in await db.Nodes.Where(n => n.IsCategory).ToListAsync(ct))
            byName.TryAdd(CategoryName.Key(category.Title), category);

        var edges = new HashSet<(Guid, Guid)>();
        foreach (var filing in filings)
        {
            foreach (var written in filing.Categories)
            {
                var name = CategoryName.Collapse(written);
                if (name.Length == 0 || name.Length > CategoryName.MaxLength)
                {
                    logger.LogWarning("Ignoring category '{Name}' on {Node}: not a name.",
                        written, filing.NodeId);
                    continue;
                }
                if (!byName.TryGetValue(CategoryName.Key(name), out var category))
                {
                    category = await categories.EnsureAsync(filing.OwnerId, name, ct);
                    byName[CategoryName.Key(name)] = category;
                    report.Added++;
                    logger.LogInformation(
                        "Wrote a page for category '{Name}' that only a sidecar knew.", name);
                }
                if (category.Id != filing.NodeId && edges.Add((filing.NodeId, category.Id)))
                    db.NodeCategories.Add(new NodeCategory
                    {
                        NodeId = filing.NodeId,
                        CategoryId = category.Id,
                    });
            }
        }
        await db.SaveChangesAsync(ct);

        // One snapshot for the lot, now that the graph is whole: a node's search text is
        // its categories' whole ancestry, and until this point there was no ancestry.
        var everything = await db.Nodes.Select(n => n.Id).ToListAsync(ct);
        await nodes.RefreshSearchTextAsync(everything, await CategoryIndex.LoadAsync(db, ct), ct);
    }

    /// <summary>What one indexed file said it was about, held until every root has been
    /// walked and the names can be resolved against the pages that answer to them.</summary>
    private readonly record struct Filing(Guid NodeId, Guid OwnerId,
        IReadOnlyList<string> Categories);

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

    /// <summary>How many links the rebuild derived from the bodies it read.</summary>
    public int Links { get; set; }
    public List<string> SkippedRoots { get; } = [];
}
