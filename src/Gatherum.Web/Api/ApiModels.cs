using Gatherum.Core.Domain;
using Gatherum.Core.Services;

namespace Gatherum.Web.Api;

public record NodeDto(
    Guid Id,
    string Kind,
    string Title,
    Guid? ParentId,
    int Position,
    bool IsPrivate,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Markdown,
    FileInfoDto? File)
{
    /// <summary>The one node → wire-format mapping, shared by the REST API and MCP so
    /// both surfaces always say the same thing. Markdown is the body itself for
    /// Markdown nodes; other kinds expose their extracted text in File.</summary>
    public static NodeDto From(Node node) => new(
        node.Id,
        node.Kind.ToString(),
        node.Title,
        node.ParentId,
        node.Position,
        node.IsPrivate,
        node.Tags.Select(t => t.Tag!.Name).Order().ToList(),
        node.CreatedAt,
        node.UpdatedAt,
        node is { MediaType: MediaTypes.Markdown, File.Versions.Count: > 0 }
            ? node.File.Current.ExtractedText
            : null,
        node.File is { Versions.Count: > 0 } file ? FileInfoDto.From(file) : null);
}

public record FileInfoDto(
    string FileName,
    string MediaType,
    long SizeBytes,
    int Version,
    string Sha256,
    string Description,
    string ExtractedText)
{
    public static FileInfoDto From(FileBody file) => new(
        file.Current.FileName,
        file.Current.MediaType,
        file.Current.SizeBytes,
        file.Current.Number,
        file.Current.Hash,
        file.Description,
        file.Current.ExtractedText);
}

public record NodeSummaryDto(Guid Id, string Kind, string Title, Guid? ParentId, int Position)
{
    public static NodeSummaryDto From(Node node) =>
        new(node.Id, node.Kind.ToString(), node.Title, node.ParentId, node.Position);
}

public record SimilarDto(Guid Id, string Kind, string Title)
{
    public static SimilarDto From(SimilarNode node) =>
        new(node.Id, node.Kind.ToString(), node.Title);
}

public record TreeNodeDto(Guid Id, Guid? ParentId, string Title, string MediaType, string Kind,
    int Position, bool IsPrivate)
{
    public static TreeNodeDto From(TreeNode node) => new(node.Id, node.ParentId, node.Title,
        node.MediaType, node.Kind.ToString(), node.Position, node.IsPrivate);
}

public record VersionDto(int Number, string FileName, string MediaType, long SizeBytes,
    DateTimeOffset UploadedAt, Guid UploadedById, bool IsText)
{
    public static VersionDto From(FileVersion version) => new(version.Number, version.FileName,
        version.MediaType, version.SizeBytes, version.UploadedAt, version.UploadedById,
        MediaTypes.IsText(version.MediaType, version.FileName));
}

public record KeyDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, bool IsActive)
{
    public static KeyDto From(ApiKey key) =>
        new(key.Id, key.Name, key.Prefix, key.CreatedAt, key.LastUsedAt, key.IsActive);
}

public record CreatedKeyDto(Guid Id, string Name, string Token);

public record SearchResultDto(Guid Id, string Kind, string Title, string Snippet)
{
    public static SearchResultDto From(SearchResult result) =>
        new(result.Id, result.Kind.ToString(), result.Title, result.Snippet);
}

/// <summary>A title that named a node, for [[wiki link]] resolution.</summary>
public record TitleMatchDto(string Title, Guid Id);

public record ResolveTitlesRequest(IReadOnlyList<string>? Titles);
public record CreatePageRequest(Guid? ParentId, string Title, string? Markdown);
public record UpdatePageRequest(string Markdown, string? Title);
public record SaveTextRequest(string Text);
public record MoveNodeRequest(Guid? NewParentId, int? Position);
public record RenameRequest(string Title);
public record TagRequest(string Tag);
public record CreateKeyRequest(string Name);
public record SetPrivateRequest(bool IsPrivate);
public record DescriptionRequest(string Description);
public record PresenceDto(IReadOnlyList<string> Editors, int HeadVersion);
