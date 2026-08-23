using System.Collections.Concurrent;
using System.Text;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gatherum.Core.Services;

/// <summary>Everything about node bodies: bytes in content-addressed storage, versions
/// as the one history mechanism, text editing, and extraction. A "page" is just a
/// Markdown file created here.</summary>
public class FileService(
    GatherumDbContext db,
    NodeService nodes,
    IFileStorage storage,
    UserRoots roots,
    IEnumerable<ITextExtractor> extractors,
    IEnumerable<IMediaAnalyzer> analyzers,
    MediaAnalysisQueue analysisQueue,
    TimeProvider clock,
    ILogger<FileService> logger)
{
    /// <summary>Rapid text saves by the same author fold into the latest version instead
    /// of flooding history with keystroke-sized snapshots.</summary>
    private static readonly TimeSpan VersionCollapseWindow = TimeSpan.FromMinutes(5);

    /// <summary>Two editors autosave the same node concurrently; serializing saves per
    /// node keeps version numbers and link rows race-free. Process-wide is enough:
    /// Gatherum deploys as a single instance.</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> SaveGates = new();

    public async Task<Node> CreateTextNodeAsync(Guid userId, Guid? parentId, string title,
        string content = "", string mediaType = MediaTypes.Markdown, CancellationToken ct = default)
    {
        var node = await nodes.CreateNodeAsync(userId, parentId, title, mediaType, ct);
        node.File = new FileBody { NodeId = node.Id };
        // A page is a file, so it gets a real name on disk. When the title cannot be one,
        // the bytes still land somewhere sane and the title lives on in the node.
        var extension = MediaTypes.ExtensionFor(mediaType);
        node.RelativePath = await AllocatePathAsync(userId, parentId,
            NodePaths.FileNameFor(title, extension) ?? $"{node.Id:N}{extension}", ct);
        await AddTextVersionAsync(node, userId, content, ct);
        await db.SaveChangesAsync(ct);
        return node;
    }

    public async Task<Node> CreateFileNodeAsync(Guid userId, Guid? parentId, string fileName,
        string? declaredMediaType, Stream content, CancellationToken ct = default)
    {
        var mediaType = MediaTypes.Resolve(declaredMediaType, fileName);
        var node = await nodes.CreateNodeAsync(userId, parentId, fileName, mediaType, ct);
        node.File = new FileBody { NodeId = node.Id };
        node.RelativePath = await AllocatePathAsync(userId, parentId,
            NodePaths.IsLegalSegment(fileName) ? fileName : $"{node.Id:N}", ct);
        var version = await AddUploadedVersionAsync(node, userId, fileName, mediaType, content, ct);
        await db.SaveChangesAsync(ct);
        QueueAnalysis(version);
        return node;
    }

    public async Task<Node> UploadVersionAsync(Guid userId, Guid nodeId, string fileName,
        string? declaredMediaType, Stream content, CancellationToken ct = default)
    {
        var gate = GateFor(nodeId);
        await gate.WaitAsync(ct);
        try
        {
            var node = await RequireFileNodeAsync(userId, nodeId, ct);
            var mediaType = MediaTypes.Resolve(declaredMediaType, fileName);
            var version = await AddUploadedVersionAsync(node, userId, fileName, mediaType, content, ct);
            node.MediaType = mediaType;
            await db.SaveChangesAsync(ct);
            QueueAnalysis(version);
            return node;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Saves edited text as a new version (or folds into the latest one during
    /// rapid autosave). This is how pages — and any text file — get edited.</summary>
    public async Task<FileVersion> SaveTextAsync(Guid userId, Guid nodeId, string content,
        CancellationToken ct = default)
    {
        var gate = GateFor(nodeId);
        await gate.WaitAsync(ct);
        try
        {
            var node = await RequireFileNodeAsync(userId, nodeId, ct);
            if (!MediaTypes.IsText(node.MediaType, node.File!.Current.FileName))
                throw new ForbiddenException($"Node {nodeId} is not editable text.");

            var bytes = Encoding.UTF8.GetBytes(content);
            var current = node.File.Current;
            await ArchiveCurrentAsync(node, ct);
            var blob = await WriteWorkingAsync(node, new MemoryStream(bytes), ct);
            var now = clock.GetUtcNow();

            if (current.UploadedById == userId && now - current.UploadedAt < VersionCollapseWindow)
            {
                current.Hash = blob.Hash;
                current.SizeBytes = blob.SizeBytes;
                current.ExtractedText = content;
            }
            else
            {
                AddVersion(node, new FileVersion
                {
                    Id = Guid.NewGuid(),
                    NodeId = node.Id,
                    Number = current.Number + 1,
                    Hash = blob.Hash,
                    MediaType = node.MediaType,
                    FileName = current.FileName,
                    SizeBytes = blob.SizeBytes,
                    ExtractedText = content,
                    UploadedById = userId,
                    UploadedAt = now,
                });
            }

            node.UpdatedAt = now;
            nodes.RefreshSearchText(node);
            await RefreshLinksAsync(node, userId, ct);
            await db.SaveChangesAsync(ct);
            return node.File.Current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The binary door beside <see cref="SaveTextAsync"/>: saves an edited
    /// rich-document body (docx today) with the same autosave collapse. Search text
    /// comes from the format's extractor, not the bytes.</summary>
    public async Task<FileVersion> SaveBinaryAsync(Guid userId, Guid nodeId, byte[] content,
        CancellationToken ct = default)
    {
        var gate = GateFor(nodeId);
        await gate.WaitAsync(ct);
        try
        {
            var node = await RequireFileNodeAsync(userId, nodeId, ct);
            if (node.MediaType != MediaTypes.Docx)
                throw new ForbiddenException($"Node {nodeId} is not an editable document.");

            var current = node.File!.Current;
            await ArchiveCurrentAsync(node, ct);
            var blob = await WriteWorkingAsync(node, new MemoryStream(content), ct);
            var text = await ExtractTextAsync(node, node.MediaType, current.FileName, ct);
            var now = clock.GetUtcNow();

            if (current.UploadedById == userId && now - current.UploadedAt < VersionCollapseWindow)
            {
                current.Hash = blob.Hash;
                current.SizeBytes = blob.SizeBytes;
                current.ExtractedText = text;
            }
            else
            {
                AddVersion(node, new FileVersion
                {
                    Id = Guid.NewGuid(),
                    NodeId = node.Id,
                    Number = current.Number + 1,
                    Hash = blob.Hash,
                    MediaType = node.MediaType,
                    FileName = current.FileName,
                    SizeBytes = blob.SizeBytes,
                    ExtractedText = text,
                    UploadedById = userId,
                    UploadedAt = now,
                });
            }

            node.UpdatedAt = now;
            nodes.RefreshSearchText(node);
            await RefreshLinksAsync(node, userId, ct);
            await db.SaveChangesAsync(ct);
            return node.File.Current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reading a version back into the present: the old content becomes the
    /// newest version. Now that the working file is the system of record this is a byte
    /// copy as well as a row insert — the archive still supplies the bytes for free, but
    /// somebody looking at the directory has to see the restored document there.</summary>
    public async Task<Node> RestoreVersionAsync(Guid userId, Guid nodeId, int versionNumber,
        CancellationToken ct = default)
    {
        var gate = GateFor(nodeId);
        await gate.WaitAsync(ct);
        try
        {
            var node = await RequireFileNodeAsync(userId, nodeId, ct);
            var version = node.File!.Versions.FirstOrDefault(v => v.Number == versionNumber)
                ?? throw new NotFoundException($"Version {versionNumber} of node {nodeId} not found.");

            // Put the outgoing content in the archive before overwriting it, then write
            // the restored bytes where the document actually lives.
            await ArchiveCurrentAsync(node, ct);
            var path = await PathAsync(node, ct);
            await using (var archived = await storage.OpenArchiveAsync(path.Root, version.Hash, ct))
            {
                await storage.WriteAsync(path, archived, ct);
            }

            var restored = new FileVersion
            {
                Id = Guid.NewGuid(),
                NodeId = node.Id,
                Number = node.File.Current.Number + 1,
                Hash = version.Hash,
                MediaType = version.MediaType,
                FileName = version.FileName,
                SizeBytes = version.SizeBytes,
                ExtractedText = version.ExtractedText,
                Transcript = version.Transcript,
                Summary = version.Summary,
                Analysis = version.Analysis,
                AnalysisError = version.AnalysisError,
                UploadedById = userId,
                UploadedAt = clock.GetUtcNow(),
            };
            AddVersion(node, restored);
            node.MediaType = version.MediaType;
            node.UpdatedAt = clock.GetUtcNow();
            nodes.RefreshSearchText(node);
            await RefreshLinksAsync(node, userId, ct);
            await db.SaveChangesAsync(ct);
            QueueAnalysis(restored);
            return node;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The background analyzer's door back in. Keyed by version rather than by
    /// user because nothing here is a request: the bytes were authorized when they were
    /// admitted at upload, and the worker acts for the system. Takes the node's save
    /// gate so a transcript landing mid-autosave cannot race a version being written.
    /// A version deleted while its model was still thinking is a no-op, not an error.</summary>
    public Task ApplyAnalysisAsync(Guid versionId, MediaAnalysis analysis,
        CancellationToken ct = default) =>
        RecordAnalysisAsync(versionId, version =>
        {
            version.Transcript = analysis.Transcript;
            version.Summary = analysis.Summary;
            version.Analysis = MediaAnalysisState.Complete;
            version.AnalysisError = "";
        }, ct);

    public Task FailAnalysisAsync(Guid versionId, string error, CancellationToken ct = default) =>
        RecordAnalysisAsync(versionId, version =>
        {
            version.Analysis = MediaAnalysisState.Failed;
            version.AnalysisError = Truncate(error, 1000);
        }, ct);

    private async Task RecordAnalysisAsync(Guid versionId, Action<FileVersion> record,
        CancellationToken ct)
    {
        var nodeId = await db.FileVersions.Where(v => v.Id == versionId)
            .Select(v => (Guid?)v.NodeId).FirstOrDefaultAsync(ct);
        if (nodeId is not { } id)
            return;

        var gate = GateFor(id);
        await gate.WaitAsync(ct);
        try
        {
            var node = await db.Nodes
                .Include(n => n.Categories).ThenInclude(c => c.Category)
                .Include(n => n.File!).ThenInclude(f => f.Versions)
                .FirstOrDefaultAsync(n => n.Id == id, ct);
            var version = node?.File?.Versions.FirstOrDefault(v => v.Id == versionId);
            if (node is null || version is null)
                return;

            record(version);
            // Deliberately not touching UpdatedAt: a model finishing its work is not
            // somebody editing, and Recent should not reshuffle hours after an upload.
            nodes.RefreshSearchText(node);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Media that predates analysis being switched on: the current version of
    /// every node an analyzer claims but nobody ever asked it about. Marks them Pending
    /// and hands back their ids, so turning an endpoint on makes the photos already in
    /// the tree searchable rather than only the next one uploaded. Older versions are
    /// left alone — history is for reading back, not for spending an afternoon of a
    /// model on.</summary>
    public async Task<List<Guid>> BackfillAnalysisAsync(CancellationToken ct = default)
    {
        if (!analyzers.Any())
            return [];

        var candidates = await db.FileVersions
            .Where(v => v.Analysis == MediaAnalysisState.None)
            .Where(v => v.Number == db.FileVersions
                .Where(other => other.NodeId == v.NodeId)
                .Max(other => other.Number))
            .Select(v => new { v.Id, v.MediaType, v.FileName })
            .ToListAsync(ct);

        var claimed = candidates
            .Where(c => analyzers.Any(a => a.CanAnalyze(c.MediaType, c.FileName)))
            .Select(c => c.Id)
            .ToList();
        if (claimed.Count > 0)
            await db.FileVersions
                .Where(v => claimed.Contains(v.Id))
                .ExecuteUpdateAsync(
                    set => set.SetProperty(v => v.Analysis, MediaAnalysisState.Pending), ct);
        return claimed;
    }

    /// <summary>Every version still waiting on a model, oldest first — what the worker
    /// sweeps at startup so a restart mid-transcript resumes instead of stranding.</summary>
    public Task<List<Guid>> PendingAnalysisIdsAsync(CancellationToken ct = default) =>
        db.FileVersions
            .Where(v => v.Analysis == MediaAnalysisState.Pending)
            .OrderBy(v => v.UploadedAt)
            .Select(v => v.Id)
            .ToListAsync(ct);

    public async Task SetDescriptionAsync(Guid userId, Guid nodeId, string description,
        CancellationToken ct = default)
    {
        var node = await RequireFileNodeAsync(userId, nodeId, ct);
        node.File!.Description = description;
        node.UpdatedAt = clock.GetUtcNow();
        nodes.RefreshSearchText(node);
        await RefreshLinksAsync(node, userId, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<FileContent> OpenContentAsync(Guid userId, Guid nodeId, int? versionNumber = null,
        CancellationToken ct = default)
    {
        var node = await RequireFileNodeAsync(userId, nodeId, ct);
        var version = versionNumber is { } number
            ? node.File!.Versions.FirstOrDefault(v => v.Number == number)
                ?? throw new NotFoundException($"Version {number} of node {nodeId} not found.")
            : node.File!.Current;
        var stream = await OpenVersionAsync(node, version, ct);
        return new FileContent(stream, version.MediaType, version.FileName, version.SizeBytes);
    }

    /// <summary>The latest version number, cheaply — the editor polls this to notice
    /// someone else's save.</summary>
    public async Task<int> GetHeadVersionAsync(Guid userId, Guid nodeId, CancellationToken ct = default)
    {
        await nodes.GetVisibleAsync(userId, nodeId, ct);
        return await db.FileVersions
            .Where(v => v.NodeId == nodeId)
            .MaxAsync(v => (int?)v.Number, ct) ?? 0;
    }

    /// <summary>The editable text of a text node — read from storage, so it is exact
    /// even where extraction truncates.</summary>
    public async Task<string> GetTextAsync(Guid userId, Guid nodeId, CancellationToken ct = default)
    {
        var content = await OpenContentAsync(userId, nodeId, null, ct);
        await using (content.Stream)
        using (var reader = new StreamReader(content.Stream, Encoding.UTF8))
        {
            return await reader.ReadToEndAsync(ct);
        }
    }

    private async Task<Node> RequireFileNodeAsync(Guid userId, Guid nodeId, CancellationToken ct)
    {
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        if (node.File is null || node.File.Versions.Count == 0)
            throw new NotFoundException($"Node {nodeId} has no file body.");
        return node;
    }

    private async Task AddTextVersionAsync(Node node, Guid userId, string content, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var blob = await WriteWorkingAsync(node, new MemoryStream(bytes), ct);
        AddVersion(node, new FileVersion
        {
            Id = Guid.NewGuid(),
            NodeId = node.Id,
            Number = 1,
            Hash = blob.Hash,
            MediaType = node.MediaType,
            FileName = FileNameFor(node),
            SizeBytes = blob.SizeBytes,
            ExtractedText = content,
            UploadedById = userId,
            UploadedAt = clock.GetUtcNow(),
        });
        nodes.RefreshSearchText(node);
        await RefreshLinksAsync(node, userId, ct);
    }

    private async Task<FileVersion> AddUploadedVersionAsync(Node node, Guid userId, string fileName,
        string mediaType, Stream content, CancellationToken ct)
    {
        await ArchiveCurrentAsync(node, ct);
        var blob = await WriteWorkingAsync(node, content, ct);
        var text = await ExtractTextAsync(node, mediaType, fileName, ct);
        var analysis = await PlanAnalysisAsync(blob.Hash, mediaType, fileName, ct);
        var version = new FileVersion
        {
            Id = Guid.NewGuid(),
            NodeId = node.Id,
            Number = node.File!.Versions.Count == 0 ? 1 : node.File.Current.Number + 1,
            Hash = blob.Hash,
            MediaType = mediaType,
            FileName = fileName,
            SizeBytes = blob.SizeBytes,
            ExtractedText = text,
            Transcript = analysis.Transcript,
            Summary = analysis.Summary,
            Analysis = analysis.State,
            UploadedById = userId,
            UploadedAt = clock.GetUtcNow(),
        };
        AddVersion(node, version);
        node.UpdatedAt = clock.GetUtcNow();
        nodes.RefreshSearchText(node);
        await RefreshLinksAsync(node, userId, ct);
        return version;
    }

    /// <summary>What a new version already knows about its own analysis before any model
    /// runs. Content-addressing pays off twice here: identical bytes uploaded again
    /// inherit the transcript a model already spent minutes on, so only genuinely new
    /// media ever queues.</summary>
    private async Task<AnalysisPlan> PlanAnalysisAsync(string hash, string mediaType,
        string fileName, CancellationToken ct)
    {
        if (!analyzers.Any(a => a.CanAnalyze(mediaType, fileName)))
            return new AnalysisPlan(MediaAnalysisState.None, "", "");

        var known = await db.FileVersions
            .Where(v => v.Hash == hash && v.Analysis == MediaAnalysisState.Complete)
            .Select(v => new { v.Transcript, v.Summary })
            .FirstOrDefaultAsync(ct);
        return known is null
            ? new AnalysisPlan(MediaAnalysisState.Pending, "", "")
            : new AnalysisPlan(MediaAnalysisState.Complete, known.Transcript, known.Summary);
    }

    private void QueueAnalysis(FileVersion version)
    {
        if (version.Analysis == MediaAnalysisState.Pending)
            analysisQueue.Enqueue(version.Id);
    }

    private record AnalysisPlan(MediaAnalysisState State, string Transcript, string Summary);

    /// <summary>The link rows a body claims. <paramref name="userId"/> is whose eyes
    /// resolve a <c>[[wiki link]]</c>: it names a page rather than pointing at one, so
    /// it can only mean a node the person writing it can see.</summary>
    private async Task RefreshLinksAsync(Node node, Guid userId, CancellationToken ct)
    {
        var targets = new HashSet<Guid>(MarkdownContent.MentionedNodeIds(node.File!.Description));
        // A docx body's extracted text is its canonical Markdown rendering, so mentions
        // inserted in the document editor link — and backlink — the same way pages do.
        if (node.MediaType is MediaTypes.Markdown or MediaTypes.Docx)
        {
            var body = node.File.Current.ExtractedText;
            targets.UnionWith(MarkdownContent.LinkedNodeIds(body));
            var wikiTargets = WikiLinkSyntax.Targets(body);
            if (wikiTargets.Count > 0)
                targets.UnionWith((await nodes.ResolveTitlesAsync(userId, wikiTargets, ct)).Values);
        }
        await nodes.ReplaceLinksAsync(node, targets, ct);
    }

    private async Task<string> ExtractTextAsync(Node node, string mediaType, string fileName,
        CancellationToken ct)
    {
        var extractor = extractors.FirstOrDefault(e => e.CanExtract(mediaType, fileName));
        if (extractor is null)
            return "";
        try
        {
            await using var stream = await storage.OpenReadAsync(await PathAsync(node, ct), ct);
            return await extractor.ExtractAsync(stream, mediaType, fileName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A malformed file should still upload; it just won't be findable by content.
            logger.LogWarning(ex, "Text extraction failed for {FileName} ({MediaType})",
                fileName, mediaType);
            return "";
        }
    }

    /// <summary>The analyzer's way to the bytes. Keyed by version rather than by user
    /// for the same reason the write-back is: nothing here is a request, and the bytes
    /// were authorized when they were admitted at upload.</summary>
    public async Task<Stream> OpenVersionAsync(Guid versionId, CancellationToken ct = default)
    {
        var version = await db.FileVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new NotFoundException($"Version {versionId} not found.");
        var node = await db.Nodes.Include(n => n.File!).ThenInclude(f => f.Versions)
            .FirstOrDefaultAsync(n => n.Id == version.NodeId, ct)
            ?? throw new NotFoundException($"Node {version.NodeId} not found.");
        return await OpenVersionAsync(node, version, ct);
    }

    private async Task<NodePath> PathAsync(Node node, CancellationToken ct) =>
        new(await roots.ForAsync(node.OwnerId, ct), node.RelativePath);

    /// <summary>Writes the working file — the node's current content, and the thing that
    /// survives losing everything else.</summary>
    private async Task<StoredBlob> WriteWorkingAsync(Node node, Stream content, CancellationToken ct) =>
        await storage.WriteAsync(await PathAsync(node, ct), content, ct);

    /// <summary>Moves the content about to be replaced into the archive. Only superseded
    /// bytes are archived: the newest version lives in the working file, which is the
    /// asymmetry the whole design turns on — delete the archive and you lose history,
    /// never a document. Content-addressing makes this idempotent, so a reverted page
    /// costs nothing the second time.</summary>
    private async Task ArchiveCurrentAsync(Node node, CancellationToken ct)
    {
        if (node.File is not { Versions.Count: > 0 } body)
            return;
        var path = await PathAsync(node, ct);
        if (!await storage.ExistsAsync(path, ct))
            return;
        if (await storage.ArchivedAsync(path.Root, body.Current.Hash, ct))
            return;
        await using var stream = await storage.OpenReadAsync(path, ct);
        await storage.ArchiveAsync(path.Root, stream, ct);
    }

    /// <summary>The current version is the file on disk; every older one is in the
    /// archive. Falling back to the archive for the current version covers the window
    /// where a reindex has seen a file that history has not caught up with.</summary>
    private async Task<Stream> OpenVersionAsync(Node node, FileVersion version, CancellationToken ct)
    {
        var path = await PathAsync(node, ct);
        if (version.Number == node.File!.Current.Number && await storage.ExistsAsync(path, ct))
            return await storage.OpenReadAsync(path, ct);
        return await storage.OpenArchiveAsync(path.Root, version.Hash, ct);
    }

    /// <summary>Where a new node's bytes go: inside its parent's child directory, under a
    /// name nothing else in there has taken.</summary>
    private async Task<string> AllocatePathAsync(Guid userId, Guid? parentId, string fileName,
        CancellationToken ct)
    {
        var directory = "";
        if (parentId is { } id)
        {
            var parent = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (parent is not null)
                directory = NodePaths.ChildDirectory(parent);
        }
        var siblings = await db.Nodes
            .Where(n => n.OwnerId == userId && n.ParentId == parentId)
            .Select(n => n.RelativePath)
            .ToListAsync(ct);
        var taken = siblings.Select(p => p.Split('/')[^1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NodePaths.Combine(directory, NodePaths.Deduplicate(fileName, taken.Contains));
    }

    /// <summary>Versions carry their Guid key from birth, so EF must be told they are
    /// new — entities discovered through a tracked collection would count as Modified.</summary>
    private void AddVersion(Node node, FileVersion version)
    {
        node.File!.Versions.Add(version);
        if (db.Entry(version).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            db.FileVersions.Add(version);
    }

    private static string FileNameFor(Node node)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(node.Title.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        var extension = node.MediaType == MediaTypes.Markdown ? ".md" : ".txt";
        return (safe.Length == 0 ? "untitled" : safe) + extension;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static SemaphoreSlim GateFor(Guid nodeId) =>
        SaveGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
}

public record FileContent(Stream Stream, string MediaType, string FileName, long SizeBytes);
