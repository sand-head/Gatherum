using System.Net;
using System.Text;
using Gatherum.Infrastructure.Bookmarks;

namespace Gatherum.Tests;

/// <summary>The browser archiver against a real headless Chromium and a local page that
/// only exists once its script has run — which is the whole reason the browser is
/// there. Machines with no Chromium skip: the fallback these tests would prove is
/// <see cref="HttpPageArchiver"/>, and it has its own coverage.</summary>
public sealed class BrowserPageArchiverTests : IDisposable
{
    private static readonly byte[] Png =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly HttpListener server = new();
    private readonly string origin;
    private readonly string adOrigin;
    private readonly CancellationTokenSource stopping = new();

    public BrowserPageArchiverTests()
    {
        var port = FreePort();
        origin = $"http://127.0.0.1:{port}";
        // The same listener under a second name, so a page on 127.0.0.1 can reference
        // an "ad host" that is really just localhost — no live network in these tests.
        adOrigin = $"http://localhost:{port}";
        server.Prefixes.Add($"{origin}/");
        server.Prefixes.Add($"{adOrigin}/");
        server.Start();
        _ = ServeAsync();
        // The environment may route outbound traffic through a proxy; the page under
        // test lives on the loopback and must not take that detour. A NO_PROXY already
        // present is presumed to say so itself — and is other tests' to keep.
        if (Environment.GetEnvironmentVariable("NO_PROXY") is null)
            Environment.SetEnvironmentVariable("NO_PROXY", "127.0.0.1,localhost");
    }

    public void Dispose()
    {
        stopping.Cancel();
        server.Close();
    }

    private static BrowserPageArchiver? Archiver(AdBlocklist? ads = null)
    {
        var browser = BrowserPageArchiver.ResolveBrowser("");
        if (browser is null)
            return null;
        ads ??= AdBlocklist.None;
        var fallback = new HttpPageArchiver(new HttpClient(), TimeProvider.System, ads);
        return new BrowserPageArchiver(browser, fallback, ads, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrowserPageArchiver>.Instance);
    }

    [Fact]
    public async Task Captures_the_page_scripts_made_not_the_page_the_server_sent()
    {
        if (Archiver() is not { } archiver)
            return;

        var page = await archiver.ArchiveAsync(new Uri($"{origin}/"));
        var html = Encoding.UTF8.GetString(page.Content);

        // The server sent a stub; everything asserted here was written by the script.
        Assert.Equal("Rendered Title", page.Title);
        Assert.Contains("rendered by the script", html);
        // The image the script injected was recorded off the wire and folded in.
        Assert.Contains($"src=\"data:image/png;base64,{Convert.ToBase64String(Png)}\"", html);
        // The stylesheet folded in too, its background image with it.
        Assert.Contains("background:url", html);
        Assert.Contains("data:image/png;base64,", html);
        Assert.DoesNotContain("<script", html);
        Assert.Contains($"saved from {origin}/", html);
    }

    [Fact]
    public async Task A_url_serving_a_document_falls_back_to_the_plain_fetch()
    {
        if (Archiver() is not { } archiver)
            return;

        var page = await archiver.ArchiveAsync(new Uri($"{origin}/manual.pdf"));

        Assert.Equal("application/pdf", page.MediaType);
        Assert.Equal("manual.pdf", page.FileName);
        Assert.Equal([0x25, 0x50, 0x44, 0x46], page.Content);
    }

    [Fact]
    public async Task A_listed_host_is_refused_before_its_script_can_draw()
    {
        if (Archiver(new AdBlocklist(["localhost"])) is not { } archiver)
            return;

        var page = await archiver.ArchiveAsync(new Uri($"{origin}/ad-laden"));
        var html = Encoding.UTF8.GetString(page.Content);

        Assert.Contains("the article itself", html);
        // The script was aborted at the network, so what it would have drawn was
        // never in the DOM — not removed from the capture, never captured.
        Assert.DoesNotContain("the ad drew", html);
        // And the banner it served is gone too, not even left as a live link.
        Assert.DoesNotContain("banner.png", html);
    }

    private async Task ServeAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await server.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }
            var (type, body) = context.Request.Url?.AbsolutePath switch
            {
                "/" => ("text/html", Encoding.UTF8.GetBytes("""
                    <html><head><title>Static Title</title>
                    <link rel="stylesheet" href="/site.css">
                    <script>
                        addEventListener('DOMContentLoaded', () => {
                            document.title = 'Rendered Title';
                            const p = document.createElement('p');
                            p.textContent = 'rendered by the script';
                            document.body.append(p);
                            const img = document.createElement('img');
                            img.src = '/pic.png';
                            document.body.append(img);
                        });
                    </script></head>
                    <body><p>static stub</p></body></html>
                    """)),
                "/site.css" => ("text/css", "body{background:url('/bg.png')}"u8.ToArray()),
                "/pic.png" or "/bg.png" or "/banner.png" => ("image/png", Png),
                "/ad-laden" => ("text/html", Encoding.UTF8.GetBytes($"""
                    <html><head><title>Article</title>
                    <script src="{adOrigin}/ad.js"></script></head>
                    <body><p>the article itself</p>
                    <img src="{adOrigin}/banner.png"></body></html>
                    """)),
                "/ad.js" => ("text/javascript", Encoding.UTF8.GetBytes("""
                    addEventListener('DOMContentLoaded', () => {
                        const p = document.createElement('p');
                        p.textContent = 'the ad drew';
                        document.body.append(p);
                    });
                    """)),
                "/manual.pdf" => ("application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }),
                _ => ("text/plain", Array.Empty<byte>()),
            };
            context.Response.ContentType = type;
            if (context.Request.Url?.AbsolutePath == "/manual.pdf")
                context.Response.AddHeader("Content-Disposition", "attachment; filename=manual.pdf");
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
