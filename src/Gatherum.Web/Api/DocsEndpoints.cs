using System.Text;
using Gatherum.Core.Domain;
using Gatherum.Web.Auth;
using Gatherum.Web.Services;

namespace Gatherum.Web.Api;

/// <summary>
/// The manual as text rather than as a page: what you hand a model. The HTML lives in
/// <c>Components/Pages/DocsPage.razor</c>; these are the same words with nothing
/// rendered around them, at URLs a fetch tool can be pointed at.
///
/// Unauthenticated on purpose — the manual is identical in every install and says
/// nothing about what is in this one — but under the same per-address budget the rest of
/// the anonymous surface answers within.
/// </summary>
public static class DocsEndpoints
{
    public static void MapGatherumDocs(this WebApplication app)
    {
        var docs = app.MapGroup(DocsLibrary.Root)
            .AllowAnonymous()
            .RequireRateLimiting(AnonymousRateLimits.Read);

        docs.MapGet("/llms.txt", (DocsLibrary library, HttpContext http) =>
            Results.Text(library.LlmsTxt(Origin(http)), "text/plain", Encoding.UTF8));

        docs.MapGet("/all.md", (DocsLibrary library, HttpContext http) =>
            Results.Text(library.Manual(Origin(http)), MediaTypes.Markdown, Encoding.UTF8));

        // A body on the 404 on purpose: an empty one would be re-executed into the
        // browser's not-found page, and what asks for a .md is not a browser.
        docs.MapGet("/{slug}.md", (DocsLibrary library, HttpContext http, string slug) =>
            library.Find(slug) is { } page
                ? Results.Text(page.Markdown, MediaTypes.Markdown, Encoding.UTF8)
                : Results.Text($"No such documentation page. The index is at {Origin(http)}{DocsLibrary.Root}/llms.txt\n",
                    "text/plain", Encoding.UTF8, StatusCodes.Status404NotFound));
    }

    /// <summary>Where this instance answers, as a link that will still work when the
    /// answer is read somewhere else entirely. Behind a proxy this is only as good as
    /// the forwarded headers, which is the same bargain the rate limiter makes.</summary>
    private static string Origin(HttpContext http) =>
        $"{http.Request.Scheme}://{http.Request.Host}";
}
