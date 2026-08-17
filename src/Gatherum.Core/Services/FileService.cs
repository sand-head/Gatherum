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
    IEnumerable<ITextExtractor> extractors,
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
        await AddUploadedVersionAsync(node, userId, fileName, mediaType, content, ct);
        await db.SaveChangesAsync(ct);
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
            await AddUploadedVersionAsync(node, userId, fileName, mediaType, content, ct);
            node.MediaType = mediaType;
            await db.SaveChangesAsync(ct);
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
            var blob = await storage.SaveAsync(new MemoryStream(bytes), ct);
            var current = node.File.Current;
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
            await RefreshLinksAsync(node, ct);
            await db.SaveChangesAsync(ct);
            return node.File.Current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reading a version back into the present: the old blob becomes the newest
    /// version. Content-addressing makes this a row insert, not a byte copy.</summary>
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

            AddVersion(node, new FileVersion
            {
                Id = Guid.NewGuid(),
                NodeId = node.Id,
                Number = node.File.Current.Number + 1,
                Hash = version.Hash,
                MediaType = version.MediaType,
                FileName = version.FileName,
                SizeBytes = version.SizeBytes,
                ExtractedText = version.ExtractedText,
                UploadedById = userId,
                UploadedAt = clock.GetUtcNow(),
            });
            node.MediaType = version.MediaType;
            node.UpdatedAt = clock.GetUtcNow();
            nodes.RefreshSearchText(node);
            await RefreshLinksAsync(node, ct);
            await db.SaveChangesAsync(ct);
            return node;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetDescriptionAsync(Guid userId, Guid nodeId, string description,
        CancellationToken ct = default)
    {
        var node = await RequireFileNodeAsync(userId, nodeId, ct);
        node.File!.Description = description;
        node.UpdatedAt = clock.GetUtcNow();
        nodes.RefreshSearchText(node);
        await RefreshLinksAsync(node, ct);
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
        var stream = await storage.OpenReadAsync(version.Hash, ct);
        return new FileContent(stream, version.MediaType, version.FileName, version.SizeBytes);
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
        var blob = await storage.SaveAsync(new MemoryStream(bytes), ct);
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
        await RefreshLinksAsync(node, ct);
    }

    private async Task AddUploadedVersionAsync(Node node, Guid userId, string fileName,
        string mediaType, Stream content, CancellationToken ct)
    {
        var blob = await storage.SaveAsync(content, ct);
        var text = await ExtractTextAsync(blob.Hash, mediaType, fileName, ct);
        AddVersion(node, new FileVersion
        {
            Id = Guid.NewGuid(),
            NodeId = node.Id,
            Number = node.File!.Versions.Count == 0 ? 1 : node.File.Current.Number + 1,
            Hash = blob.Hash,
            MediaType = mediaType,
            FileName = fileName,
            SizeBytes = blob.SizeBytes,
            ExtractedText = text,
            UploadedById = userId,
            UploadedAt = clock.GetUtcNow(),
        });
        node.UpdatedAt = clock.GetUtcNow();
        nodes.RefreshSearchText(node);
        await RefreshLinksAsync(node, ct);
    }

    private async Task RefreshLinksAsync(Node node, CancellationToken ct)
    {
        var targets = new HashSet<Guid>(MarkdownContent.MentionedNodeIds(node.File!.Description));
        if (node.MediaType == MediaTypes.Markdown)
            targets.UnionWith(MarkdownContent.LinkedNodeIds(node.File.Current.ExtractedText));
        await nodes.ReplaceLinksAsync(node, targets, ct);
    }

    private async Task<string> ExtractTextAsync(string hash, string mediaType, string fileName,
        CancellationToken ct)
    {
        var extractor = extractors.FirstOrDefault(e => e.CanExtract(mediaType, fileName));
        if (extractor is null)
            return "";
        try
        {
            await using var stream = await storage.OpenReadAsync(hash, ct);
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

    private static SemaphoreSlim GateFor(Guid nodeId) =>
        SaveGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
}

public record FileContent(Stream Stream, string MediaType, string FileName, long SizeBytes);
