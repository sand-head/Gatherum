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
    CategoryService categories,
    FileService files,
    SearchService search,
    IHttpContextAccessor httpContext)
{
    private Guid UserId => httpContext.HttpContext?.User.GetUserId()
        ?? throw new McpException("No authenticated user.");

    [McpServerTool(Name = "search")]
    [Description("Search titles, categories, and file text (pages are Markdown files). " +
        "Combines full-text matching with meaning-based matching, so a query finds pages " +
        "that answer it without using its words. Supports websearch syntax: quoted " +
        "phrases, OR, -exclusions — which only the full-text half honours.")]
    public async Task<IEnumerable<SearchResultDto>> Search(
        [Description("The search query.")] string query,
        [Description("Optional filter: 'page' (Markdown), 'file' (everything else), or " +
            "'category' (a subject's own page).")]
        string? kind = null,
        [Description("Maximum results, default 20.")] int? limit = null,
        [Description("'hybrid' (default), 'text' for literal matching only, or " +
            "'semantic' for meaning only. Use 'text' when the exact spelling matters, " +
            "such as an identifier or a quoted phrase.")]
        string? mode = null)
    {
        NodeKind? nodeKind = kind is null ? null : ParseKind(kind);
        var searchMode = mode is null
            ? SearchMode.Hybrid
            : Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var parsed)
                ? parsed
                : throw new McpException($"Unknown search mode '{mode}'.");
        var results = await search.SearchAsync(UserId, query, nodeKind, limit ?? 20, searchMode);
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

    [McpServerTool(Name = "add_category")]
    [Description("File a node under a category, by name. A category is a page: if none " +
        "is called this yet, one is written. Filing a category under another category is " +
        "what makes it a subcategory, and a node in a subcategory counts as a member of " +
        "everything above it.")]
    public async Task<string> AddCategory(
        [Description("The node id.")] Guid id,
        [Description("The category name, e.g. 'Podman'.")] string name) =>
        await Run(() => categories.AddAsync(UserId, id, name));

    [McpServerTool(Name = "remove_category")]
    [Description("Take a node out of one category. Its other categories, and the " +
        "categories this one is nested under, are untouched.")]
    public async Task<string> RemoveCategory(
        [Description("The node id.")] Guid id,
        [Description("The category name to remove.")] string name)
    {
        await Run(async () =>
        {
            await categories.RemoveAsync(UserId, id, name);
            return true;
        });
        return "removed";
    }

    [McpServerTool(Name = "list_categories")]
    [Description("Every category, by name, with the ids of the categories each is nested " +
        "under, how many nodes sit in it directly, and how many its subcategories hold " +
        "in total. A category can be nested under more than one.")]
    public async Task<IEnumerable<CategoryDto>> ListCategories(
        [Description("Optional filter: only categories whose name contains this text.")]
        string? matching = null)
    {
        var all = await categories.ListAsync(UserId, matching);
        return all.Select(CategoryDto.From);
    }

    [McpServerTool(Name = "browse_category")]
    [Description("One category: what it is nested under, what is nested under it, and " +
        "the nodes in it — the subcategories' nodes too when deep is true. The category " +
        "is a node of its own; read its page with get_node for what it says it holds.")]
    public async Task<CategoryViewDto> BrowseCategory(
        [Description("The category name, e.g. 'Podman'.")] string name,
        [Description("Include the nodes of every subcategory, default false.")]
        bool? deep = null) =>
        await Run(async () => CategoryViewDto.From(
            await categories.GetAsync(UserId, name, deep ?? false)));

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
            : throw new McpException(
                $"Unknown kind '{kind}'; use 'page', 'file' or 'category'.");

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
        catch (ValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
