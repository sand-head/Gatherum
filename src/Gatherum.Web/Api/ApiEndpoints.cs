using Gatherum.Core;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Epub;
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
            var results = await search.SearchAsync(http.User.GetUserIdOrNull(), query, nodeKind,
                limit ?? 20, searchMode);
            return Results.Ok(results.Select(SearchResultDto.From));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Search);

        api.MapGet("/nodes/tree", async (NodeService nodes, HttpContext http) =>
        {
            var tree = await nodes.GetTreeAsync(http.User.GetUserIdOrNull());
            return Results.Ok(tree.Select(TreeNodeDto.From));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        api.MapGet("/nodes/{id:guid}", async (NodeService nodes, HttpContext http, Guid id) =>
            Results.Ok(NodeDto.From(await nodes.GetWithBodyAsync(http.User.GetUserIdOrNull(), id))))
            .AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        api.MapGet("/nodes/{id:guid}/children", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            var children = await nodes.GetChildrenAsync(http.User.GetUserIdOrNull(), id);
            return Results.Ok(children.Select(NodeSummaryDto.From));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        api.MapGet("/nodes/roots", async (NodeService nodes, HttpContext http) =>
        {
            var roots = await nodes.GetChildrenAsync(http.User.GetUserIdOrNull(), null);
            return Results.Ok(roots.Select(NodeSummaryDto.From));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

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

        api.MapPost("/nodes/{id:guid}/access", async (AccessService access, HttpContext http,
            Guid id, SetAccessRequest request) =>
        {
            if (!Enum.TryParse<AccessMode>(request.Access, ignoreCase: true, out var mode))
                return Results.BadRequest(new { error = $"Unknown access '{request.Access}'." });
            await access.SetAccessAsync(http.User.GetUserId(), id, mode, request.Inherit);
            return Results.NoContent();
        });

        api.MapGet("/nodes/{id:guid}/grants", async (AccessService access, HttpContext http,
            Guid id) => Results.Ok((await access.ListGrantsAsync(http.User.GetUserId(), id))
                .Select(GrantDto.From)));

        // Who there is to share with. Names and ids only, and only for somebody already
        // signed in — a list of the people on an instance is not public.
        api.MapGet("/users", async (UserService users) =>
            Results.Ok((await users.ListAsync()).Select(UserDto.From)));

        api.MapPost("/nodes/{id:guid}/grants", async (AccessService access, HttpContext http,
            Guid id, GrantRequest request) =>
        {
            if (!Enum.TryParse<AccessRole>(request.Role, ignoreCase: true, out var role))
                return Results.BadRequest(new { error = $"Unknown role '{request.Role}'." });
            await access.GrantAsync(http.User.GetUserId(), id, request.UserId, role);
            return Results.NoContent();
        });

        api.MapDelete("/nodes/{id:guid}/grants/{userId:guid}", async (AccessService access,
            HttpContext http, Guid id, Guid userId) =>
        {
            await access.RevokeAsync(http.User.GetUserId(), id, userId);
            return Results.NoContent();
        });

        api.MapPost("/nodes/{id:guid}/categories", async (CategoryService categories,
            HttpContext http, Guid id, CategoryRequest request) =>
        {
            var name = await categories.AddAsync(http.User.GetUserId(), id, request.Name);
            return Results.Ok(new { name });
        });

        api.MapDelete("/nodes/{id:guid}/categories/{name}", async (CategoryService categories,
            HttpContext http, Guid id, string name) =>
        {
            await categories.RemoveAsync(http.User.GetUserId(), id, name);
            return Results.NoContent();
        });

        api.MapGet("/categories", async (CategoryService categories, HttpContext http,
            string? matching) =>
        {
            var all = await categories.ListAsync(http.User.GetUserId(), matching);
            return Results.Ok(all.Select(CategoryDto.From));
        });

        // There is no rename, move or delete here any more, and that is the change rather
        // than an omission: a category is a page, so it is renamed by PATCHing its node,
        // re-nested by filing that node under another category, and deleted by deleting
        // it. Three endpoints became none.
        //
        // A category is a page: its own body, the categories it is nested under, what is
        // nested under it, and its members — the subcategories' members too when deep asks.
        api.MapGet("/categories/{name}", async (CategoryService categories, HttpContext http,
            string name, bool? deep) =>
            Results.Ok(CategoryViewDto.From(
                await categories.GetAsync(http.User.GetUserId(), name, deep ?? false))));

        // Titles, not ids: what a [[wiki link]] has to ask before it can go anywhere.
        // Anonymous, because a public page's wiki links are read by whoever the page was
        // published for, and the authorizer answers them with what that visitor may see.
        api.MapPost("/nodes/resolve-titles", async (NodeService nodes, HttpContext http,
            ResolveTitlesRequest request) =>
        {
            var resolved = await nodes.ResolveTitlesAsync(http.User.GetUserIdOrNull(),
                request.Titles ?? []);
            return Results.Ok(resolved.Select(m => new TitleMatchDto(m.Key, m.Value)));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        // Ids, not titles: which of the nodes a page links its reader may open. What
        // comes back missing is drawn locked instead of as a link.
        api.MapPost("/nodes/reachable", async (NodeService nodes, HttpContext http,
            ReachableRequest request) =>
        {
            var reachable = await nodes.ReachableIdsAsync(http.User.GetUserIdOrNull(),
                request.Ids ?? []);
            return Results.Ok(reachable);
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        api.MapGet("/nodes/{id:guid}/backlinks", async (NodeService nodes, HttpContext http, Guid id) =>
        {
            var backlinks = await nodes.GetBacklinksAsync(http.User.GetUserIdOrNull(), id);
            return Results.Ok(backlinks.Select(NodeSummaryDto.From));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        api.MapGet("/nodes/{id:guid}/similar", async (NodeService nodes, HttpContext http,
            Guid id, int? limit) =>
        {
            var similar = await nodes.GetSimilarAsync(http.User.GetUserIdOrNull(), id, limit ?? 5);
            return Results.Ok(similar.Select(SimilarDto.From));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

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

        api.MapPost("/bookmarks", async (BookmarkService bookmarks, NodeService nodes,
            HttpContext http, BookmarkRequest request) =>
        {
            var node = await bookmarks.SaveAsync(http.User.GetUserId(), request.ParentId,
                request.Url);
            var created = await nodes.GetWithBodyAsync(http.User.GetUserId(), node.Id);
            return Results.Created($"/api/nodes/{node.Id}", NodeDto.From(created));
        });

        api.MapPost("/bookmarks/{id:guid}/capture", async (BookmarkService bookmarks,
            NodeService nodes, HttpContext http, Guid id) =>
        {
            await bookmarks.CaptureAgainAsync(http.User.GetUserId(), id);
            return Results.Ok(NodeDto.From(await nodes.GetWithBodyAsync(http.User.GetUserId(), id)));
        });

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
            var content = await files.OpenContentAsync(http.User.GetUserIdOrNull(), id, version);
            // Stored markup served inline on the app's own origin would run as whoever
            // opened it. Sandboxing the response keeps a bookmark snapshot — or any
            // uploaded HTML or SVG — a document rather than a program, both in the file
            // view's frame and opened directly.
            if (content.MediaType is MediaTypes.Html or "image/svg+xml")
                http.Response.Headers.ContentSecurityPolicy = "sandbox";
            return Results.Stream(content.Stream, content.MediaType,
                enableRangeProcessing: true);
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        api.MapGet("/files/{id:guid}/download", async (FileService files, HttpContext http,
            Guid id, int? version) =>
        {
            var content = await files.OpenContentAsync(http.User.GetUserIdOrNull(), id, version);
            return Results.Stream(content.Stream, content.MediaType,
                fileDownloadName: content.FileName);
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        // The reader's map of an EPUB: the book's own title and its chapters, in
        // reading order, named the way its table of contents names them.
        api.MapGet("/files/{id:guid}/epub", async (FileService files, HttpContext http,
            Guid id, int? version) =>
        {
            var content = await files.OpenContentAsync(http.User.GetUserIdOrNull(), id, version);
            await using var stream = content.Stream;
            if (!IsEpub(content))
                return Results.NotFound();
            using var book = await EpubBook.OpenAsync(stream);
            var position = await files.GetReadingPositionAsync(http.User.GetUserIdOrNull(), id);
            var served = version
                ?? await files.GetHeadVersionAsync(http.User.GetUserIdOrNull(), id);
            // Never cached: it carries the reader's own position, and it is what
            // hands out the version and renderer keys the chapter URLs pin.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(EpubDto.From(book, position, served));
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        // The ribbon moves as the reader reads. A write, so never anonymous: a
        // stranger on a public book reads without being remembered.
        api.MapPut("/files/{id:guid}/epub/position", async (FileService files,
            HttpContext http, Guid id, EpubPositionRequest request) =>
        {
            await files.SaveReadingPositionAsync(http.User.GetUserId(), id,
                request.Chapter, request.Progress);
            return Results.NoContent();
        });

        // One chapter, rendered for the reader's sandboxed frame: self-contained,
        // inert but for the pager the policy admits by hash, paginated by the chrome
        // the rendering wears.
        api.MapGet("/files/{id:guid}/epub/{chapter:int}", async (FileService files,
            HttpContext http, Guid id, int chapter, int? version) =>
        {
            var content = await files.OpenContentAsync(http.User.GetUserIdOrNull(), id, version);
            await using var stream = content.Stream;
            if (!IsEpub(content))
                return Results.NotFound();
            using var book = await EpubBook.OpenAsync(stream);
            if (chapter < 0 || chapter >= book.Chapters.Count)
                return Results.NotFound();
            var html = await EpubChapterHtml.RenderAsync(book, chapter);
            http.Response.Headers.ContentSecurityPolicy = EpubChapterHtml.ContentSecurityPolicy;
            // A request that pinned the book's version may keep the answer forever —
            // the reader's URLs also carry the render stamp, so everything the bytes
            // depend on is in the key. The bare URL means "the latest", which no
            // cache can promise: an unpinned chapter cached across a deploy is
            // exactly how a phone keeps swiping against last week's pager.
            http.Response.Headers.CacheControl = version is null
                ? "no-store"
                : "private, max-age=31536000, immutable";
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        api.MapPut("/files/{id:guid}/description", async (FileService files, HttpContext http,
            Guid id, DescriptionRequest request) =>
        {
            await files.SetDescriptionAsync(http.User.GetUserId(), id, request.Description);
            return Results.NoContent();
        });

        // A collection list, whichever page it is asked from: a catalog aggregates
        // itself, a tally aggregates the catalog it tracks. Anonymous, because reading
        // a published list is reading a published page — and the columns that come back
        // are the tallies this visitor may enumerate and no others.
        api.MapGet("/nodes/{id:guid}/collection", async (CollectionService collections,
            HttpContext http, Guid id, string? list) =>
            Results.Ok(CollectionDto.From(
                await collections.GetAsync(http.User.GetUserIdOrNull(), id, list))))
            .AllowAnonymous().RequireRateLimiting(AnonymousRateLimits.Read);

        // Answering writes the caller's own tally and nobody else's, so there is no
        // anonymous door here — a column in a shared grid is somebody's file.
        api.MapPost("/nodes/{id:guid}/collection", async (CollectionService collections,
            HttpContext http, Guid id, CollectAnswerRequest request) =>
            Results.Ok(CollectionDto.From(await collections.SetAsync(http.User.GetUserId(), id,
                request.Key, request.Collected, request.List))));

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

    /// <summary>What the reader endpoints serve: the stored type when the upload was
    /// honest, the extension when a browser called the book a plain zip or nothing at
    /// all — the same benefit of the doubt extraction gives it.</summary>
    private static bool IsEpub(FileContent content) =>
        content.MediaType.Equals(MediaTypes.Epub, StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(content.FileName).Equals(".epub", StringComparison.OrdinalIgnoreCase);

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
