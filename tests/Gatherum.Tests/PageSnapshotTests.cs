using System.Text;
using Gatherum.Infrastructure.Bookmarks;

namespace Gatherum.Tests;

/// <summary>The transform that makes a fetched page worth keeping: inert, self-contained,
/// and honest about where it came from. Pure — the "web" is a dictionary.</summary>
public class PageSnapshotTests
{
    private static readonly Uri Page = new("https://example.org/posts/thermals");
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static Task<PageSnapshot.Result> BuildAsync(string html,
        Dictionary<string, FetchedAsset>? assets = null) =>
        PageSnapshot.BuildAsync(Page, Encoding.UTF8.GetBytes(html),
            (url, _) => Task.FromResult(assets?.GetValueOrDefault(url.AbsoluteUri)),
            CapturedAt);

    private static string Text(PageSnapshot.Result result) =>
        Encoding.UTF8.GetString(result.Content);

    [Fact]
    public async Task Scripts_handlers_and_frames_do_not_survive_capture()
    {
        var result = await BuildAsync("""
            <html><head><script src="/app.js"></script></head>
            <body onload="boot()">
              <iframe src="https://ads.example/frame"></iframe>
              <a href="javascript:alert(1)" onclick="track()">click</a>
              <p>the closet runs hot</p>
            </body></html>
            """);

        var html = Text(result);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("onload", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains("the closet runs hot", html);
    }

    [Fact]
    public async Task Relative_references_become_absolute_and_a_base_tag_is_honored_then_dropped()
    {
        var result = await BuildAsync("""
            <html><head><base href="https://cdn.example.org/assets/"></head>
            <body>
              <a href="/about">about</a>
              <img src="pic.png" srcset="pic.png 1x, big.png 2x">
              <a href="mailto:jess@example.org">write</a>
            </body></html>
            """);

        // A declared base is what everything relative meant, root-relative included.
        var html = Text(result);
        Assert.Contains("href=\"https://cdn.example.org/about\"", html);
        Assert.Contains("src=\"https://cdn.example.org/assets/pic.png\"", html);
        Assert.Contains("https://cdn.example.org/assets/big.png 2x", html);
        Assert.Contains("href=\"mailto:jess@example.org\"", html);
        Assert.DoesNotContain("<base", html);
    }

    [Fact]
    public async Task Stylesheets_fold_in_with_their_own_relative_urls_resolved()
    {
        var result = await BuildAsync(
            """<html><head><link rel="stylesheet" href="/css/site.css"></head><body>hi</body></html>""",
            new Dictionary<string, FetchedAsset>
            {
                ["https://example.org/css/site.css"] = new("text/css",
                    Encoding.UTF8.GetBytes("body{background:url('../img/bg.png')}")),
            });

        var html = Text(result);
        Assert.Contains("<style>", html);
        Assert.Contains("url('https://example.org/img/bg.png')", html);
        Assert.DoesNotContain("rel=\"stylesheet\"", html);
    }

    [Fact]
    public async Task What_a_stylesheet_points_at_folds_in_too()
    {
        var result = await BuildAsync("""
            <html><head>
              <link rel="stylesheet" href="/css/type.css">
              <style>h1 { background: url(/img/band.png); }</style>
            </head><body>hi</body></html>
            """,
            new Dictionary<string, FetchedAsset>
            {
                ["https://example.org/css/type.css"] = new("text/css",
                    "@font-face{src:url('serif.woff2')}"u8.ToArray()),
                ["https://example.org/css/serif.woff2"] = new("font/woff2", [9, 9]),
                ["https://example.org/img/band.png"] = new("image/png", [7]),
            });

        var html = Text(result);
        Assert.Contains($"src:url('data:font/woff2;base64,{Convert.ToBase64String([9, 9])}')",
            html);
        Assert.Contains($"url(data:image/png;base64,{Convert.ToBase64String([7])})", html);
    }

    [Fact]
    public async Task Lazy_loading_is_dropped_because_the_scroll_already_happened()
    {
        var result = await BuildAsync(
            """<html><body><img src="https://example.org/late.png" loading="lazy"></body></html>""");
        Assert.DoesNotContain("loading=", Text(result));
    }

    [Fact]
    public async Task Images_fold_in_as_data_uris_and_a_dead_one_stays_a_live_link()
    {
        var result = await BuildAsync("""
            <html><body>
              <img src="/rack.png">
              <img src="/gone.png">
            </body></html>
            """,
            new Dictionary<string, FetchedAsset>
            {
                ["https://example.org/rack.png"] = new("image/png", [1, 2, 3]),
            });

        var html = Text(result);
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String([1, 2, 3])}\"", html);
        Assert.Contains("src=\"https://example.org/gone.png\"", html);
    }

    [Fact]
    public async Task The_first_line_says_where_and_when_and_the_title_is_the_pages_own()
    {
        var result = await BuildAsync(
            "<html><head><title> Closet thermals </title></head><body>hot</body></html>");

        Assert.Equal("Closet thermals", result.Title);
        Assert.Contains("saved from https://example.org/posts/thermals by Gatherum on 2026-08-25",
            Text(result));
    }

    [Fact]
    public async Task A_page_with_no_title_answers_to_its_address()
    {
        var result = await BuildAsync("<html><body>untitled</body></html>");
        Assert.Equal("example.org/posts/thermals", result.Title);
    }

    [Fact]
    public async Task A_latin1_page_is_reread_correctly_and_rewritten_as_declared_utf8()
    {
        // Genuinely Latin-1 bytes, as a server of a certain age would send them.
        var latin1 = System.Text.Encoding.Latin1.GetBytes("""
            <html><head><meta charset="iso-8859-1"><title>café</title></head>
            <body>déjà vu</body></html>
            """);
        var result = await PageSnapshot.BuildAsync(Page, latin1,
            (_, _) => Task.FromResult<FetchedAsset?>(null), CapturedAt);

        var html = Text(result);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.DoesNotContain("iso-8859-1", html);
        Assert.Contains("déjà vu", html);
        Assert.Equal("café", result.Title);
    }
}
