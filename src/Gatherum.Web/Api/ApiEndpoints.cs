using Gatherum.Core;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Gatherum.Core.Services;
using Gatherum.Web.Auth;

namespace Gatherum.Web.Api;

public static class ApiEndpoints
{
    public static void MapGatherumApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("Api");
        api.AddEndpointFilter(TranslateDomainErrors);

        api.MapGet("/search", async (SearchService search, HttpContext http,
            string query, string? kind, int? limit) =>
        {
            NodeKind? nodeKind = kind is null ? null : Enum.Parse<NodeKind>(kind, ignoreCase: true);
            var results = await search.SearchAsync(http.User.GetUserId(), query, nodeKind, limit ?? 20);
            return Results.Ok(results.Select(SearchResultDto.From));
        });

        api.MapGet("/nodes/tree", async (NodeService nodes, HttpContext http) =>
            Results.Ok(await nodes.GetTreeAsync(http.User.GetUserId())));

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

        api.MapPost("/pages", async (NodeService nodes, HttpContext http, CreatePageRequest request) =>
        {
            var doc = request.Markdown is null ? null : PageMarkdown.ToDocJson(request.Markdown);
            var node = await nodes.CreatePageAsync(http.User.GetUserId(), request.ParentId,
                request.Title, doc);
            var created = await nodes.GetWithBodyAsync(http.User.GetUserId(), node.Id);
            return Results.Created($"/api/nodes/{node.Id}", NodeDto.From(created));
        });

        api.MapPut("/pages/{id:guid}", async (NodeService nodes, HttpContext http, Guid id,
            UpdatePageRequest request) =>
        {
            var node = await nodes.SavePageAsync(http.User.GetUserId(), id,
                PageMarkdown.ToDocJson(request.Markdown), request.Title, resetCollabState: true);
            return Results.Ok(NodeDto.From(node));
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

        api.MapPost("/nodes/{id:guid}/tags", async (NodeService nodes, HttpContext http, Guid id,
            TagRequest request) =>
        {
            await nodes.AddTagAsync(http.User.GetUserId(), id, request.Tag);
            return Results.NoContent();
        });

        api.MapDelete("/nodes/{id:guid}/tags/{tag}", async (NodeService nodes, HttpContext http,
            Guid id, string tag) =>
        {
            await nodes.RemoveTagAsync(http.User.GetUserId(), id, tag);
            return Results.NoContent();
        });

        api.MapGet("/tags", async (NodeService nodes, HttpContext http, string? prefix) =>
            Results.Ok(await nodes.ListTagsAsync(http.User.GetUserId(), prefix)));

        api.MapGet("/tags/{tag}/nodes", async (NodeService nodes, HttpContext http, string tag) =>
        {
            var tagged = await nodes.GetNodesWithTagAsync(http.User.GetUserId(), tag);
            return Results.Ok(tagged.Select(NodeSummaryDto.From));
        });

        api.MapGet("/nodes/{id:guid}/backlinks", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            var backlinks = await nodes.GetBacklinksAsync(http.User.GetUserId(), id);
            return Results.Ok(backlinks.Select(NodeSummaryDto.From));
        });

        api.MapGet("/nodes/{id:guid}/revisions", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            var revisions = await nodes.GetRevisionsAsync(http.User.GetUserId(), id);
            return Results.Ok(revisions.Select(RevisionDto.From));
        });

        api.MapGet("/nodes/{id:guid}/revisions/{number:int}", async (NodeService nodes,
            HttpContext http, Guid id, int number) =>
        {
            var revisions = await nodes.GetRevisionsAsync(http.User.GetUserId(), id);
            var revision = revisions.FirstOrDefault(r => r.Number == number)
                ?? throw new NotFoundException($"Revision {number} of node {id} not found.");
            return Results.Ok(new
            {
                revision.Number,
                revision.Title,
                revision.CreatedAt,
                Markdown = PageMarkdown.ToMarkdown(revision.Doc),
            });
        });

        api.MapPost("/nodes/{id:guid}/revisions/{number:int}/restore", async (NodeService nodes,
            HttpContext http, Guid id, int number) =>
        {
            var node = await nodes.RestoreRevisionAsync(http.User.GetUserId(), id, number);
            return Results.Ok(NodeDto.From(node));
        });

        api.MapPost("/files", async (FileService files, NodeService nodes, HttpContext http,
            IFormFile file, Guid? parentId) =>
        {
            await using var stream = file.OpenReadStream();
            var node = await files.CreateFileNodeAsync(http.User.GetUserId(), parentId,
                file.FileName, ContentType(file), stream);
            var created = await nodes.GetWithBodyAsync(http.User.GetUserId(), node.Id);
            return Results.Created($"/api/nodes/{node.Id}", NodeDto.From(created));
        }).DisableAntiforgery();

        api.MapPost("/files/{id:guid}/versions", async (FileService files, NodeService nodes,
            HttpContext http, Guid id, IFormFile file) =>
        {
            await using var stream = file.OpenReadStream();
            await files.UploadVersionAsync(http.User.GetUserId(), id, file.FileName,
                ContentType(file), stream);
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
            return Results.Ok(list.Select(k => new
            {
                k.Id, k.Name, k.Prefix, k.CreatedAt, k.RevokedAt, k.LastUsedAt,
            }));
        });

        api.MapPost("/keys", async (ApiKeyService keys, HttpContext http, CreateKeyRequest request) =>
        {
            var created = await keys.CreateAsync(http.User.GetUserId(), request.Name);
            return Results.Ok(new { created.Key.Id, created.Key.Name, Token = created.PlaintextToken });
        });

        api.MapDelete("/keys/{id:guid}", async (ApiKeyService keys, HttpContext http, Guid id) =>
        {
            await keys.RevokeAsync(http.User.GetUserId(), id);
            return Results.NoContent();
        });
    }

    private static string ContentType(IFormFile file) =>
        string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

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
    }
}
