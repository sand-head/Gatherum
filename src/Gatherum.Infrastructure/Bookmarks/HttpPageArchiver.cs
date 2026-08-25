using System.Net.Http;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Services;

namespace Gatherum.Infrastructure.Bookmarks;

/// <summary>Captures a URL with a plain HTTP fetch: the page as the server serves it,
/// folded into one file by <see cref="PageSnapshot"/>. What a script would have drawn
/// afterwards is not in the capture — a browser-driving archiver is the second
/// implementation this seam exists for. A URL that serves a document rather than a page
/// (a PDF, an image) is kept as itself.
///
/// Everything here is bounded, because the other end is somebody else's server: one time
/// budget for the whole capture, a ceiling on the page, a ceiling per asset, and a purse
/// for assets overall. Blowing the page's own bounds fails the capture with a sentence
/// for the person who pasted the URL; a failed or oversized asset just stays a link to
/// the live web.</summary>
public class HttpPageArchiver(HttpClient http, TimeProvider clock) : IPageArchiver
{
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(30);
    private const long MaxPageBytes = 10 * 1024 * 1024;
    private const long MaxDocumentBytes = 128L * 1024 * 1024;
    private const long MaxAssetBytes = 4 * 1024 * 1024;
    private const long AssetPurseBytes = 24 * 1024 * 1024;

    public async Task<ArchivedPage> ArchiveAsync(Uri url, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CaptureBudget);
        try
        {
            return await FetchAsync(url, budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PageArchiveException(
                $"{url} took longer than {CaptureBudget.TotalSeconds:0} seconds to capture.");
        }
        catch (HttpRequestException ex)
        {
            throw new PageArchiveException($"Could not fetch {url}: {ex.Message}", ex);
        }
    }

    private async Task<ArchivedPage> FetchAsync(Uri url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new PageArchiveException(
                $"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        // Redirects were followed to get here; the snapshot's references resolve
        // against where the page actually was.
        var finalUrl = response.RequestMessage?.RequestUri ?? url;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";

        if (mediaType is "text/html" or "application/xhtml+xml")
            return await SnapshotAsync(finalUrl, response, ct);

        var bytes = await ReadCappedAsync(response, MaxDocumentBytes, ct)
            ?? throw new PageArchiveException(
                $"{url} is larger than a bookmark will hold ({MaxDocumentBytes / (1024 * 1024)} MB).");
        var fileName = DocumentFileName(finalUrl, response);
        return new ArchivedPage(NodePaths.DefaultTitle(fileName), fileName,
            mediaType.Length > 0 ? mediaType : null, bytes);
    }

    private async Task<ArchivedPage> SnapshotAsync(Uri url, HttpResponseMessage response,
        CancellationToken ct)
    {
        var html = await ReadCappedAsync(response, MaxPageBytes, ct)
            ?? throw new PageArchiveException(
                $"{url} is larger than a bookmark will hold ({MaxPageBytes / (1024 * 1024)} MB).");

        var purse = AssetPurseBytes;
        var snapshot = await PageSnapshot.BuildAsync(url, html, async (asset, token) =>
        {
            if (purse <= 0)
                return null;
            var fetched = await FetchAssetAsync(asset, Math.Min(MaxAssetBytes, purse), token);
            if (fetched is not null)
                purse -= fetched.Content.Length;
            return fetched;
        }, clock.GetUtcNow(), ct);

        var fileName = NodePaths.FileNameFor(snapshot.Title, ".html")
            ?? NodePaths.FileNameFor(url.Host, ".html")
            ?? "bookmark.html";
        return new ArchivedPage(snapshot.Title, fileName, "text/html", snapshot.Content);
    }

    /// <summary>One asset, best-effort: anything wrong with it — unreachable, refused,
    /// over its ceiling — leaves the snapshot pointing at the live URL instead.</summary>
    private async Task<FetchedAsset?> FetchAssetAsync(Uri url, long cap, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            var bytes = await ReadCappedAsync(response, cap, ct);
            return bytes is null
                ? null
                : new FetchedAsset(
                    response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
                    bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Reads a body up to a ceiling, or null past it — Content-Length is
    /// checked first but never trusted alone, because a server that lies about it would
    /// otherwise stream without limit.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long cap,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > cap)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > cap)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string DocumentFileName(Uri url, HttpResponseMessage response)
    {
        var suggested = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (suggested is { Length: > 0 } && NodePaths.IsLegalSegment(suggested))
            return suggested;
        var segment = Uri.UnescapeDataString(url.Segments[^1].Trim('/'));
        if (segment.Length > 0 && NodePaths.IsLegalSegment(segment))
            return segment;
        return url.Host;
    }
}
