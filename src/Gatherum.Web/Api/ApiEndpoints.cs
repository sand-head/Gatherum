using Gatherum.Core;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Gatherum.Web.Auth;
using Gatherum.Web.Services;

namespace Gatherum.Web.Api;

public static class ApiEndpoints
{
    public static void MapGatherumApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("Api");
        api.AddEndpointFilter(TranslateDomainErrors);

        api.MapGet("/search", async (SearchService search, HttpContext http,
            string query, string? kind, int? limit, string? mode) =>
        {
            NodeKind? nodeKind = kind is null ? null : Enum.Parse<NodeKind>(kind, ignoreCase: true);
            var searchMode = mode is null
                ? SearchMode.Hybrid
                : Enum.Parse<SearchMode>(mode, ignoreCase: true);
            var results = await search.SearchAsync(http.User.GetUserId(), query, nodeKind,
                limit ?? 20, searchMode);
            return Results.Ok(results.Select(SearchResultDto.From));
        });

        api.MapGet("/nodes/tree", async (NodeService nodes, HttpContext http) =>
        {
            var tree = await nodes.GetTreeAsync(http.User.GetUserId());
            return Results.Ok(tree.Select(TreeNodeDto.From));
        });

        api.MapGet("/nodes/{id:guid}", async (NodeService nodes, HttpContext http, Guid id) =>
            Results.Ok(NodeDto.From(await nodes.GetWithBodyAsync(http.User.GetUserId(), id))));

        api.MapGet("/nodes/{id:guid}/children", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            var children = await nodes.GetChildrenAsync(http.User.GetUserId(), id);
            return Results.Ok(children.Select(NodeSummaryDto.From));
        });

        api.MapGet("/nodes/roots", async (NodeService nodes, HttpContext http) =>
        {
            var roots = await nodes.GetChildrenAsync(http.User.GetUserId(), null);
            return Results.Ok(roots.Select(NodeSummaryDto.From));
        });

        api.MapPost("/pages", async (FileService files, NodeService nodes, HttpContext http,
            CreatePageRequest request) =>
        {
            var node = await files.CreateTextNodeAsync(http.User.GetUserId(), request.ParentId,
                request.Title, request.Markdown ?? "");
            var created = await nodes.GetWithBodyAsync(http.User.GetUserId(), node.Id);
            return Results.Created($"/api/nodes/{node.Id}", NodeDto.From(created));
        });

        api.MapPut("/pages/{id:guid}", async (FileService files, NodeService nodes,
            HttpContext http, Guid id, UpdatePageRequest request) =>
        {
            var userId = http.User.GetUserId();
            if (request.Title is not null)
                await nodes.RenameAsync(userId, id, request.Title);
            await files.SaveTextAsync(userId, id, request.Markdown);
            return Results.Ok(NodeDto.From(await nodes.GetWithBodyAsync(userId, id)));
        });

        // The editor island saves any editable text node — pages included — through here.
        api.MapPut("/text/{id:guid}", async (FileService files, HttpContext http, Guid id,
            SaveTextRequest request) =>
        {
            var version = await files.SaveTextAsync(http.User.GetUserId(), id, request.Text);
            return Results.Ok(new { version = version.Number });
        });

        // The binary sibling of /text: the editor island saves an edited rich
        // document (docx) through here, raw bytes in the body.
        api.MapPut("/binary/{id:guid}", async (FileService files, HttpContext http, Guid id) =>
        {
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer);
            var version = await files.SaveBinaryAsync(http.User.GetUserId(), id, buffer.ToArray());
            return Results.Ok(new { version = version.Number });
        });

        // The editor's WebAssembly home reaches presence through these; the server
        // home talks to the tracker directly.
        api.MapGet("/nodes/{id:guid}/presence", async (PresenceTracker presence, FileService files,
            HttpContext http, Guid id, bool? editing) =>
        {
            var userId = http.User.GetUserId();
            if (editing == true)
                presence.Heartbeat(id, userId, http.User.Identity?.Name ?? "someone");
            var head = await files.GetHeadVersionAsync(userId, id);
            return Results.Ok(new PresenceDto(presence.OthersEditing(id, userId), head));
        });

        api.MapPost("/nodes/{id:guid}/presence/leave", (PresenceTracker presence,
            HttpContext http, Guid id) =>
        {
            presence.Leave(id, http.User.GetUserId());
            return Results.NoContent();
        });

        api.MapPost("/nodes/{id:guid}/move", async (NodeService nodes, HttpContext http, Guid id,
            MoveNodeRequest request) =>
        {
            await nodes.MoveAsync(http.User.GetUserId(), id, request.NewParentId, request.Position);
            return Results.NoContent();
        });

        api.MapPost("/nodes/{id:guid}/rename", async (NodeService nodes, HttpContext http, Guid id,
            RenameRequest request) =>
        {
            await nodes.RenameAsync(http.User.GetUserId(), id, request.Title);
            return Results.NoContent();
        });

        api.MapDelete("/nodes/{id:guid}", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            await nodes.DeleteAsync(http.User.GetUserId(), id);
            return Results.NoContent();
        });

        api.MapPost("/nodes/{id:guid}/private", async (NodeService nodes, HttpContext http, Guid id,
            SetPrivateRequest request) =>
        {
            await nodes.SetPrivateAsync(http.User.GetUserId(), id, request.IsPrivate);
            return Results.NoContent();
        });

        api.MapPost("/nodes/{id:guid}/categories", async (CategoryService categories,
            HttpContext http, Guid id, CategoryRequest request) =>
        {
            var path = await categories.AddAsync(http.User.GetUserId(), id, request.Path);
            return Results.Ok(new { path });
        });

        api.MapDelete("/nodes/{id:guid}/categories/{**path}", async (CategoryService categories,
            HttpContext http, Guid id, string path) =>
        {
            await categories.RemoveAsync(http.User.GetUserId(), id, path);
            return Results.NoContent();
        });

        api.MapGet("/categories", async (CategoryService categories, HttpContext http,
            string? matching) =>
        {
            var all = await categories.ListAsync(http.User.GetUserId(), matching);
            return Results.Ok(all.Select(CategoryDto.From));
        });

        // Rename and move carry the path in the body: it is the thing being changed,
        // and a route would have to spell it twice.
        api.MapPost("/categories/rename", async (CategoryService categories,
            RenameCategoryRequest request) =>
        {
            await categories.RenameAsync(request.Path, request.Name);
            return Results.NoContent();
        });

        api.MapPost("/categories/move", async (CategoryService categories,
            MoveCategoryRequest request) =>
        {
            await categories.MoveAsync(request.Path, request.NewParentPath);
            return Results.NoContent();
        });

        api.MapDelete("/categories/{**path}", async (CategoryService categories, string path) =>
        {
            await categories.DeleteAsync(path);
            return Results.NoContent();
        });

        // A category is a page: itself, its ancestry, its subcategories and its members
        // — the subcategories' members too when deep asks.
        api.MapGet("/categories/{**path}", async (CategoryService categories, HttpContext http,
            string path, bool? deep) =>
            Results.Ok(CategoryViewDto.From(
                await categories.GetAsync(http.User.GetUserId(), path, deep ?? false))));

        // Titles, not ids: what a [[wiki link]] has to ask before it can go anywhere.
        api.MapPost("/nodes/resolve-titles", async (NodeService nodes, HttpContext http,
            ResolveTitlesRequest request) =>
        {
            var resolved = await nodes.ResolveTitlesAsync(http.User.GetUserId(),
                request.Titles ?? []);
            return Results.Ok(resolved.Select(m => new TitleMatchDto(m.Key, m.Value)));
        });

        api.MapGet("/nodes/{id:guid}/backlinks", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            var backlinks = await nodes.GetBacklinksAsync(http.User.GetUserId(), id);
            return Results.Ok(backlinks.Select(NodeSummaryDto.From));
        });

        api.MapGet("/nodes/{id:guid}/similar", async (NodeService nodes, HttpContext http,
            Guid id, int? limit) =>
        {
            var similar = await nodes.GetSimilarAsync(http.User.GetUserId(), id, limit ?? 5);
            return Results.Ok(similar.Select(SimilarDto.From));
        });

        api.MapGet("/nodes/{id:guid}/versions", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            var node = await nodes.GetWithBodyAsync(http.User.GetUserId(), id);
            var versions = (node.File?.Versions ?? []).OrderByDescending(v => v.Number);
            return Results.Ok(versions.Select(VersionDto.From));
        });

        api.MapPost("/nodes/{id:guid}/versions/{number:int}/restore", async (FileService files,
            HttpContext http, Guid id, int number) =>
        {
            var node = await files.RestoreVersionAsync(http.User.GetUserId(), id, number);
            return Results.Ok(NodeDto.From(node));
        });

        api.MapPost("/files", async (FileService files, NodeService nodes, HttpContext http,
            IFormFile file, Guid? parentId) =>
        {
            await using var stream = file.OpenReadStream();
            var node = await files.CreateFileNodeAsync(http.User.GetUserId(), parentId,
                file.FileName, file.ContentType, stream);
            var created = await nodes.GetWithBodyAsync(http.User.GetUserId(), node.Id);
            return Results.Created($"/api/nodes/{node.Id}", NodeDto.From(created));
        }).DisableAntiforgery();

        api.MapPost("/files/{id:guid}/versions", async (FileService files, NodeService nodes,
            HttpContext http, Guid id, IFormFile file) =>
        {
            await using var stream = file.OpenReadStream();
            await files.UploadVersionAsync(http.User.GetUserId(), id, file.FileName,
                file.ContentType, stream);
            var node = await nodes.GetWithBodyAsync(http.User.GetUserId(), id);
            return Results.Ok(NodeDto.From(node));
        }).DisableAntiforgery();

        api.MapGet("/files/{id:guid}/content", async (FileService files, HttpContext http,
            Guid id, int? version) =>
        {
            var content = await files.OpenContentAsync(http.User.GetUserId(), id, version);
            return Results.Stream(content.Stream, content.MediaType,
                enableRangeProcessing: true);
        });

        api.MapGet("/files/{id:guid}/download", async (FileService files, HttpContext http,
            Guid id, int? version) =>
        {
            var content = await files.OpenContentAsync(http.User.GetUserId(), id, version);
            return Results.Stream(content.Stream, content.MediaType,
                fileDownloadName: content.FileName);
        });

        api.MapPut("/files/{id:guid}/description", async (FileService files, HttpContext http,
            Guid id, DescriptionRequest request) =>
        {
            await files.SetDescriptionAsync(http.User.GetUserId(), id, request.Description);
            return Results.NoContent();
        });

        api.MapGet("/keys", async (ApiKeyService keys, HttpContext http) =>
        {
            var list = await keys.ListAsync(http.User.GetUserId());
            return Results.Ok(list.Select(KeyDto.From));
        });

        api.MapPost("/keys", async (ApiKeyService keys, HttpContext http, CreateKeyRequest request) =>
        {
            var created = await keys.CreateAsync(http.User.GetUserId(), request.Name);
            return Results.Ok(new CreatedKeyDto(created.Key.Id, created.Key.Name,
                created.PlaintextToken));
        });

        api.MapDelete("/keys/{id:guid}", async (ApiKeyService keys, HttpContext http, Guid id) =>
        {
            await keys.RevokeAsync(http.User.GetUserId(), id);
            return Results.NoContent();
        });
    }

    private static async ValueTask<object?> TranslateDomainErrors(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (NotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ForbiddenException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
