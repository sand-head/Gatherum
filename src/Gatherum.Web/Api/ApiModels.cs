using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
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
    /// both surfaces always say the same thing.</summary>
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
        node.Page is { } page ? PageMarkdown.ToMarkdown(page.Doc) : null,
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

public record RevisionDto(int Number, string Title, DateTimeOffset CreatedAt, Guid AuthorId)
{
    public static RevisionDto From(Revision revision) =>
        new(revision.Number, revision.Title, revision.CreatedAt, revision.AuthorId);
}

public record SearchResultDto(Guid Id, string Kind, string Title, string Snippet)
{
    public static SearchResultDto From(SearchResult result) =>
        new(result.Id, result.Kind.ToString(), result.Title, result.Snippet);
}

public record CreatePageRequest(Guid? ParentId, string Title, string? Markdown);
public record UpdatePageRequest(string Markdown, string? Title);
public record MoveNodeRequest(Guid? NewParentId, int? Position);
public record RenameRequest(string Title);
public record TagRequest(string Tag);
public record CreateKeyRequest(string Name);
public record SetPrivateRequest(bool IsPrivate);
public record DescriptionRequest(string Description);
