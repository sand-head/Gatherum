using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Gatherum.Core.Services;

public class FileService(
    GatherumDbContext db,
    NodeService nodes,
    IFileStorage storage,
    IEnumerable<ITextExtractor> extractors,
    TimeProvider clock,
    ILogger<FileService> logger)
{
    public async Task<Node> CreateFileNodeAsync(Guid userId, Guid? parentId, string fileName,
        string mediaType, Stream content, CancellationToken ct = default)
    {
        var node = await nodes.CreateNodeAsync(userId, parentId, fileName, NodeKind.File, ct);
        node.File = new FileBody { NodeId = node.Id };
        await AddVersionAsync(node, userId, fileName, mediaType, content, ct);
        await db.SaveChangesAsync(ct);
        return node;
    }

    public async Task<Node> UploadVersionAsync(Guid userId, Guid nodeId, string fileName,
        string mediaType, Stream content, CancellationToken ct = default)
    {
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        if (node.File is null)
            throw new NotFoundException($"Node {nodeId} is not a file.");
        await AddVersionAsync(node, userId, fileName, mediaType, content, ct);
        await db.SaveChangesAsync(ct);
        return node;
    }

    public async Task SetDescriptionAsync(Guid userId, Guid nodeId, string description,
        CancellationToken ct = default)
    {
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        if (node.File is null)
            throw new NotFoundException($"Node {nodeId} is not a file.");
        node.File.Description = description;
        node.UpdatedAt = clock.GetUtcNow();
        nodes.RefreshSearchText(node);
        await nodes.ReplaceLinksAsync(node, MentionedNodeIds(description), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<FileContent> OpenContentAsync(Guid userId, Guid nodeId, int? versionNumber = null,
        CancellationToken ct = default)
    {
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        if (node.File is null || node.File.Versions.Count == 0)
            throw new NotFoundException($"Node {nodeId} is not a file.");
        var version = versionNumber is { } number
            ? node.File.Versions.FirstOrDefault(v => v.Number == number)
                ?? throw new NotFoundException($"Version {number} of node {nodeId} not found.")
            : node.File.Current;
        var stream = await storage.OpenReadAsync(version.Hash, ct);
        return new FileContent(stream, version.MediaType, version.FileName, version.SizeBytes);
    }

    private async Task AddVersionAsync(Node node, Guid userId, string fileName, string mediaType,
        Stream content, CancellationToken ct)
    {
        var blob = await storage.SaveAsync(content, ct);
        var text = await ExtractTextAsync(blob.Hash, mediaType, fileName, ct);
        node.File!.Versions.Add(new FileVersion
        {
            Id = Guid.NewGuid(),
            NodeId = node.Id,
            Number = node.File.Versions.Count == 0 ? 1 : node.File.Current.Number + 1,
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

    private static IReadOnlySet<Guid> MentionedNodeIds(string text)
    {
        var ids = new HashSet<Guid>();
        foreach (System.Text.RegularExpressions.Match match in
            System.Text.RegularExpressions.Regex.Matches(text, @"node://([0-9a-fA-F-]{36})"))
        {
            if (Guid.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }
        return ids;
    }
}

public record FileContent(Stream Stream, string MediaType, string FileName, long SizeBytes);
