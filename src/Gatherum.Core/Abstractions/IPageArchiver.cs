namespace Gatherum.Core.Abstractions;

/// <summary>Turns a URL into bytes worth keeping — a bookmark's capture. The shipped
/// implementation fetches what the server serves and folds the page's stylesheets and
/// images into one self-contained HTML file; a second implementation would drive a real
/// browser, for pages that only exist once scripts have run. Slow and fallible by
/// nature (it is somebody else's server), so it never runs anywhere a reader waits —
/// only inside the request that asked for the capture.</summary>
public interface IPageArchiver
{
    Task<ArchivedPage> ArchiveAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>What came back: a snapshot ready to be a file version. For an HTML page,
/// <paramref name="Content"/> is the sanitized, self-contained capture and
/// <paramref name="Title"/> is the page's own; a URL that serves something else — a PDF,
/// an image — arrives as itself, because a bookmark of a document is the document.</summary>
public record ArchivedPage(string Title, string FileName, string? MediaType, byte[] Content);

/// <summary>A capture that could not be made — the server refused, timed out, or sent
/// more than a bookmark is allowed to weigh. The message is written for the person who
/// pasted the URL.</summary>
public class PageArchiveException(string message, Exception? inner = null)
    : Exception(message, inner);
