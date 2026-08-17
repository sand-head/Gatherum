using System.ComponentModel;
using Gatherum.Core;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Gatherum.Web.Api;
using Gatherum.Web.Auth;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Gatherum.Web.Mcp;

/// <summary>MCP surface over the same application services the UI and REST API use.
/// Tools translate wire input, call a service, and map the result — nothing more.</summary>
[McpServerToolType]
public class GatherumMcpTools(
    NodeService nodes,
    FileService files,
    SearchService search,
    IHttpContextAccessor httpContext)
{
    private Guid UserId => httpContext.HttpContext?.User.GetUserId()
        ?? throw new McpException("No authenticated user.");

    [McpServerTool(Name = "search")]
    [Description("Full-text search over titles, tags, and file text (pages are Markdown " +
        "files). Supports websearch syntax: quoted phrases, OR, -exclusions.")]
    public async Task<IEnumerable<SearchResultDto>> Search(
        [Description("The search query.")] string query,
        [Description("Optional filter: 'page' (Markdown) or 'file' (everything else).")]
        string? kind = null,
        [Description("Maximum results, default 20.")] int? limit = null)
    {
        NodeKind? nodeKind = kind is null ? null : ParseKind(kind);
        var results = await search.SearchAsync(UserId, query, nodeKind, limit ?? 20);
        return results.Select(SearchResultDto.From);
    }

    [McpServerTool(Name = "get_node")]
    [Description("Fetch a node by id: metadata plus the Markdown body for pages, " +
        "or extracted text and file metadata for other files.")]
    public async Task<NodeDto> GetNode([Description("The node id.")] Guid id) =>
        await Run(async () => NodeDto.From(await nodes.GetWithBodyAsync(UserId, id)));

    [McpServerTool(Name = "list_children")]
    [Description("List a node's children in tree order. Omit id for the root level.")]
    public async Task<IEnumerable<NodeSummaryDto>> ListChildren(
        [Description("Parent node id; omit for roots.")] Guid? id = null)
    {
        var children = await Run(() => nodes.GetChildrenAsync(UserId, id));
        return children.Select(NodeSummaryDto.From);
    }

    [McpServerTool(Name = "create_page")]
    [Description("Create a page — a Markdown file node. Mentions of other nodes are " +
        "written as [@Title](node://<id>) and become links.")]
    public async Task<NodeDto> CreatePage(
        [Description("The page title.")] string title,
        [Description("The page body as Markdown.")] string markdown,
        [Description("Parent node id; omit for a root-level page.")] Guid? parentId = null) =>
        await Run(async () =>
        {
            var node = await files.CreateTextNodeAsync(UserId, parentId, title, markdown);
            return NodeDto.From(await nodes.GetWithBodyAsync(UserId, node.Id));
        });

    [McpServerTool(Name = "update_page")]
    [Description("Replace a page's Markdown body. Creates a new version; old versions " +
        "stay retrievable.")]
    public async Task<NodeDto> UpdatePage(
        [Description("The page node id.")] Guid id,
        [Description("The new body as Markdown.")] string markdown,
        [Description("Optional new title.")] string? title = null) =>
        await Run(async () =>
        {
            if (title is not null)
                await nodes.RenameAsync(UserId, id, title);
            await files.SaveTextAsync(UserId, id, markdown);
            return NodeDto.From(await nodes.GetWithBodyAsync(UserId, id));
        });

    [McpServerTool(Name = "move_node")]
    [Description("Move a node (and its subtree) to a new parent and/or position.")]
    public async Task<string> MoveNode(
        [Description("The node id to move.")] Guid id,
        [Description("New parent id; omit to move to the root level.")] Guid? newParentId = null,
        [Description("Zero-based position among siblings; omit to append.")] int? position = null)
    {
        await Run(async () =>
        {
            await nodes.MoveAsync(UserId, id, newParentId, position);
            return true;
        });
        return "moved";
    }

    [McpServerTool(Name = "add_tag")]
    [Description("Add a tag to a node. Tags are normalized to lowercase.")]
    public async Task<string> AddTag(
        [Description("The node id.")] Guid id,
        [Description("The tag to add.")] string tag)
    {
        await Run(async () =>
        {
            await nodes.AddTagAsync(UserId, id, tag);
            return true;
        });
        return "tagged";
    }

    [McpServerTool(Name = "list_tags")]
    [Description("List all tags with the number of nodes carrying each.")]
    public async Task<IEnumerable<TagSummary>> ListTags() =>
        await nodes.ListTagsAsync(UserId);

    [McpServerTool(Name = "get_backlinks")]
    [Description("List the nodes whose bodies link to the given node.")]
    public async Task<IEnumerable<NodeSummaryDto>> GetBacklinks(
        [Description("The node id.")] Guid id)
    {
        var backlinks = await Run(() => nodes.GetBacklinksAsync(UserId, id));
        return backlinks.Select(NodeSummaryDto.From);
    }

    private static NodeKind ParseKind(string kind) =>
        Enum.TryParse<NodeKind>(kind, ignoreCase: true, out var parsed)
            ? parsed
            : throw new McpException($"Unknown kind '{kind}'; use 'page' or 'file'.");

    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (NotFoundException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (ForbiddenException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
