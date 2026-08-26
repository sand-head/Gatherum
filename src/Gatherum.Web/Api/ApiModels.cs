using Gatherum.Core.Domain;
using Gatherum.Core.Services;

namespace Gatherum.Web.Api;

public record NodeDto(
    Guid Id,
    string Kind,
    string Title,
    Guid? ParentId,
    int Position,
    string Access,
    IReadOnlyList<CategoryRefDto> Categories,
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
        node.Access.ToString(),
        node.Categories.Select(c => CategoryRefDto.From(c.Category!))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        node.CreatedAt,
        node.UpdatedAt,
        node is { MediaType: MediaTypes.Markdown, File.Versions.Count: > 0 }
            ? node.File.Current.ExtractedText
            : null,
        node.File is { Versions.Count: > 0 } file ? FileInfoDto.From(file) : null);
}

/// <summary>What a file is, over the wire. <c>ExtractedText</c> is what the bytes
/// literally contain; <c>Transcript</c> and <c>Summary</c> are what a model read, heard,
/// or made of a medium that contains no text at all — an agent reading a photo or a
/// recording over MCP gets the same words search does.</summary>
public record FileInfoDto(
    string FileName,
    string MediaType,
    long SizeBytes,
    int Version,
    string Sha256,
    string Description,
    string? SourceUrl,
    string ExtractedText,
    string Transcript,
    string Summary,
    string Analysis,
    string? AnalysisError)
{
    public static FileInfoDto From(FileBody file) => new(
        file.Current.FileName,
        file.Current.MediaType,
        file.Current.SizeBytes,
        file.Current.Number,
        file.Current.Hash,
        file.Description,
        file.SourceUrl.Length > 0 ? file.SourceUrl : null,
        file.Current.ExtractedText,
        file.Current.Transcript,
        file.Current.Summary,
        file.Current.Analysis.ToString(),
        file.Current.AnalysisError.Length > 0 ? file.Current.AnalysisError : null);
}

public record NodeSummaryDto(Guid Id, string Kind, string Title, Guid? ParentId, int Position)
{
    public static NodeSummaryDto From(Node node) =>
        new(node.Id, node.Kind.ToString(), node.Title, node.ParentId, node.Position);
}

/// <summary>A category as the node filed under it wears it: the name is what the chip
/// says and what addresses it, the id is the page it is.</summary>
public record CategoryRefDto(Guid Id, string Name)
{
    public static CategoryRefDto From(Node category) => new(category.Id, category.Title);
}

/// <summary>A category can be nested under more than one, so parents are a list and
/// there is no single path up to a root to report.</summary>
public record CategoryDto(Guid Id, string Name, IReadOnlyList<Guid> ParentIds, int Members,
    int SubtreeMembers)
{
    public static CategoryDto From(CategorySummary category) => new(category.Id, category.Name,
        category.ParentIds, category.Members, category.SubtreeMembers);
}

public record CategoryViewDto(CategoryDto Category, IReadOnlyList<CategoryDto> Parents,
    IReadOnlyList<CategoryDto> Subcategories, IReadOnlyList<NodeSummaryDto> Nodes)
{
    public static CategoryViewDto From(CategoryView view) => new(
        CategoryDto.From(view.Category),
        view.Parents.Select(CategoryDto.From).ToList(),
        view.Subcategories.Select(CategoryDto.From).ToList(),
        view.Nodes.Select(NodeSummaryDto.From).ToList());
}

public record SimilarDto(Guid Id, string Kind, string Title)
{
    public static SimilarDto From(SimilarNode node) =>
        new(node.Id, node.Kind.ToString(), node.Title);
}

public record TreeNodeDto(Guid Id, Guid? ParentId, string Title, string MediaType, string Kind,
    int Position, string Access, string Reach, bool Owned)
{
    public static TreeNodeDto From(TreeNode node) => new(node.Id, node.ParentId, node.Title,
        node.MediaType, node.Kind.ToString(), node.Position, node.Access.ToString(),
        node.Reach.ToString(), node.Owned);
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
public record ReachableRequest(IReadOnlyList<Guid>? Ids);
public record CreatePageRequest(Guid? ParentId, string Title, string? Markdown);
public record UpdatePageRequest(string Markdown, string? Title);
public record SaveTextRequest(string Text);
public record MoveNodeRequest(Guid? NewParentId, int? Position);
public record RenameRequest(string Title);
public record CategoryRequest(string Name);
public record CreateKeyRequest(string Name);
/// <summary>Private, Shared, or Public — and Public means the internet.</summary>
public record SetAccessRequest(string Access, bool Inherit = true);

public record GrantRequest(Guid UserId, string Role);

public record GrantDto(Guid UserId, string DisplayName, string Username, string Role)
{
    public static GrantDto From(NodeGrant grant) => new(grant.UserId,
        grant.User?.DisplayName ?? "", grant.User?.Username ?? "", grant.Role.ToString());
}

public record UserDto(Guid Id, string DisplayName, string Username)
{
    public static UserDto From(User user) => new(user.Id, user.DisplayName, user.Username);
}
/// <summary>The reader's map of a book: its own title, and one entry per spine
/// chapter — the name the book's contents give it, or null where they give none.</summary>
public record EpubDto(string? Title, IReadOnlyList<string?> Chapters)
{
    public static EpubDto From(Gatherum.Infrastructure.Epub.EpubBook book) =>
        new(book.Title, book.Chapters.Select(c => c.Title).ToList());
}

public record DescriptionRequest(string Description);
public record BookmarkRequest(string Url, Guid? ParentId);
public record PresenceDto(IReadOnlyList<string> Editors, int HeadVersion);
