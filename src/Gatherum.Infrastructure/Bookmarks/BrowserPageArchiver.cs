using System.Collections.Concurrent;
using System.Text;
using Gatherum.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Gatherum.Infrastructure.Bookmarks;

/// <summary>Captures a URL the way a person saw it: a headless Chromium loads the page,
/// its scripts run and settle, a scroll pass coaxes out what lazy loading was keeping
/// for later, and the document that gets kept is the DOM as it stands afterwards —
/// "Webpage, Complete", in one file. Every response the page pulled in during the load
/// is recorded, so the stylesheets, images and fonts folded into the snapshot are the
/// very bytes the page rendered with, not a second fetch's idea of them.
///
/// Scripts do not ride along: by capture time they have already done their work — their
/// output is the DOM being saved — and replayed against that DOM they would rebuild,
/// duplicate, or blank what they find, besides being the one thing stored markup must
/// never be allowed to do. What is kept is what they made, not what they were.
///
/// A URL that serves a document rather than a page, an instance with no browser to be
/// found, and a browser that cannot load or finish the page all fall back to
/// <see cref="HttpPageArchiver"/> — a bookmark degrades to a plain fetch, never to a
/// failure the browser alone caused. Only the site's own answer fails a capture: a 404
/// is a 404 however it is fetched.</summary>
public class BrowserPageArchiver(string executablePath, HttpPageArchiver fallback,
    TimeProvider clock, ILogger<BrowserPageArchiver> logger) : IPageArchiver
{
    /// <summary>Roomier than the plain fetch's budget: a render pays for a browser
    /// launch, script execution, and the settle waits on top of the network.</summary>
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(60);

    /// <summary>How long the page gets to reach network-idle before the capture stops
    /// waiting and takes the document as it stands — an analytics beacon that never
    /// quiets must not hold the snapshot hostage.</summary>
    private const float SettleTimeoutMs = 10_000;

    private const int ViewportWidth = 1280;
    private const int ViewportHeight = 900;

    /// <summary>A Chromium to render with, or null when none can be found — which is the
    /// registration's cue to fall back to the plain fetch. An explicit path is taken at
    /// its word; otherwise the usual Playwright install locations are searched, newest
    /// build first.</summary>
    public static string? ResolveBrowser(string configuredPath)
    {
        if (configuredPath.Length > 0)
            return File.Exists(configuredPath) ? configuredPath : null;

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "ms-playwright"),
        };
        foreach (var root in roots)
        {
            if (root is not { Length: > 0 } || !Directory.Exists(root))
                continue;
            var direct = Path.Combine(root, "chromium");
            if (File.Exists(direct))
                return direct;
            var installed = Directory.EnumerateDirectories(root, "chromium-*")
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .SelectMany(d => new[]
                {
                    Path.Combine(d, "chrome-linux", "chrome"),
                    Path.Combine(d, "chrome-win", "chrome.exe"),
                    Path.Combine(d, "chrome-mac", "Chromium.app", "Contents", "MacOS", "Chromium"),
                })
                .FirstOrDefault(File.Exists);
            if (installed is not null)
                return installed;
        }
        return null;
    }

    public async Task<ArchivedPage> ArchiveAsync(Uri url, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CaptureBudget);
        try
        {
            return await RenderAsync(url, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Rendering {Url} outlived its budget; capturing what the " +
                "server serves instead.", url);
            return await fallback.ArchiveAsync(url, ct);
        }
        catch (PlaywrightException ex)
        {
            // The browser failing — to launch, to connect, to finish — is not the page
            // failing. What a plain fetch can still get is a better bookmark than an
            // error, and the log keeps a broken browser from hiding behind the fallback.
            logger.LogWarning("Rendering {Url} failed ({Reason}); capturing what the " +
                "server serves instead.", url, FirstLine(ex.Message));
            return await fallback.ArchiveAsync(url, ct);
        }
    }

    private async Task<ArchivedPage> RenderAsync(Uri url, CancellationToken ct)
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            ExecutablePath = executablePath,
            // The container is the sandbox here: Chromium's own needs privileges a
            // rootless container doesn't grant, and /dev/shm in one is too small for it.
            ChromiumSandbox = false,
            Args = ["--disable-dev-shm-usage"],
            // The plain fetch honours HTTPS_PROXY because HttpClient does; the browser
            // only does if told, and a deployment behind an egress proxy is one place.
            Proxy = EnvironmentProxy(),
        });
        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = ViewportWidth, Height = ViewportHeight },
        });
        using var abort = ct.Register(() => _ = context.CloseAsync());

        // Everything the page pulls in while loading, kept for the snapshot to fold in:
        // the archive holds the bytes the page actually rendered with. A queue because
        // responses land on Playwright's dispatch thread, not this one.
        var loaded = new ConcurrentQueue<IResponse>();
        var page = await context.NewPageAsync();
        page.Response += (_, response) => loaded.Enqueue(response);

        IResponse? arrival;
        try
        {
            arrival = await page.GotoAsync(url.AbsoluteUri,
                new() { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });
        }
        catch (PlaywrightException ex) when (
            ex.Message.Contains("Download is starting", StringComparison.Ordinal))
        {
            // The URL serves a file, not a page; a browser adds nothing to that.
            return await fallback.ArchiveAsync(url, ct);
        }
        if (arrival is null)
            throw new PageArchiveException($"{url} did not load as a page.");
        if (arrival.Status >= 400)
            throw new PageArchiveException(
                $"{url} answered {arrival.Status} {arrival.StatusText}.");

        var mediaType = arrival.Headers.TryGetValue("content-type", out var contentType)
            ? contentType.Split(';')[0].Trim()
            : "";
        if (mediaType is not ("text/html" or "application/xhtml+xml"))
            return await fallback.ArchiveAsync(url, ct);

        await SettleAsync(page);
        var html = await page.ContentAsync();
        var finalUrl = Uri.TryCreate(page.Url, UriKind.Absolute, out var landed) ? landed : url;
        var recorded = await RecordAsync(loaded);
        await context.CloseAsync();

        var purse = HttpPageArchiver.AssetPurseBytes;
        var snapshot = await PageSnapshot.BuildAsync(finalUrl, Encoding.UTF8.GetBytes(html),
            async (asset, token) =>
            {
                if (purse <= 0)
                    return null;
                var found = recorded.GetValueOrDefault(asset.AbsoluteUri)
                    ?? await fallback.FetchAssetAsync(asset,
                        Math.Min(HttpPageArchiver.MaxAssetBytes, purse), token);
                if (found is not null && found.Content.Length <= purse)
                {
                    purse -= found.Content.Length;
                    return found;
                }
                return null;
            }, clock.GetUtcNow(), ct);

        return new ArchivedPage(snapshot.Title,
            HttpPageArchiver.HtmlFileName(snapshot.Title, finalUrl), "text/html",
            snapshot.Content);
    }

    /// <summary>Lets the page finish becoming itself: wait for the network to go quiet
    /// (bounded — some pages never stop talking), then scroll through it so lazy-loaded
    /// images are asked for, and give those a moment to arrive.</summary>
    private static async Task SettleAsync(IPage page)
    {
        await QuietlyAsync(() => page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new() { Timeout = SettleTimeoutMs }));
        await QuietlyAsync(() => page.EvaluateAsync("""
            async () => {
                const height = Math.min(document.body?.scrollHeight ?? 0, 20000);
                for (let y = 0; y <= height; y += 700) {
                    window.scrollTo(0, y);
                    await new Promise(resolve => setTimeout(resolve, 40));
                }
                window.scrollTo(0, 0);
            }
            """));
        await QuietlyAsync(() => page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new() { Timeout = 4_000 }));
    }

    /// <summary>Settling is best-effort by definition — a page that refuses to go quiet
    /// or objects to being scrolled still gets captured as it stands.</summary>
    private static async Task QuietlyAsync(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (PlaywrightException)
        {
        }
    }

    /// <summary>The bodies of what the load pulled in, keyed by URL — only what a
    /// snapshot folds in (styles, images, fonts), only what answered well, and only up
    /// to the per-asset ceiling. A body that cannot be read back (a redirect, an
    /// evicted resource) is skipped; the snapshot will fetch or leave a live link.</summary>
    private static async Task<Dictionary<string, FetchedAsset>> RecordAsync(
        ConcurrentQueue<IResponse> loaded)
    {
        var recorded = new Dictionary<string, FetchedAsset>();
        foreach (var response in loaded.ToArray())
        {
            if (recorded.ContainsKey(response.Url) || response.Status != 200)
                continue;
            var mediaType = response.Headers.TryGetValue("content-type", out var contentType)
                ? contentType.Split(';')[0].Trim()
                : "";
            if (!(mediaType.StartsWith("image/", StringComparison.Ordinal)
                || mediaType.StartsWith("font/", StringComparison.Ordinal)
                || mediaType is "text/css" or "application/font-woff"
                    or "application/font-woff2" or "application/vnd.ms-fontobject"))
                continue;
            try
            {
                var body = await response.BodyAsync();
                if (body.Length > 0 && body.Length <= HttpPageArchiver.MaxAssetBytes)
                    recorded[response.Url] = new FetchedAsset(mediaType, body);
            }
            catch (PlaywrightException)
            {
            }
        }
        return recorded;
    }

    private static Proxy? EnvironmentProxy()
    {
        var server = Environment.GetEnvironmentVariable("HTTPS_PROXY")
            ?? Environment.GetEnvironmentVariable("https_proxy")
            ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
            ?? Environment.GetEnvironmentVariable("http_proxy");
        if (server is not { Length: > 0 })
            return null;
        return new Proxy
        {
            Server = server,
            Bypass = Environment.GetEnvironmentVariable("NO_PROXY")
                ?? Environment.GetEnvironmentVariable("no_proxy"),
        };
    }

    private static string FirstLine(string message)
    {
        var line = message.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        return end < 0 ? message : message[..end];
    }
}
