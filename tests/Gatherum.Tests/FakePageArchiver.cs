using Gatherum.Core.Abstractions;

namespace Gatherum.Tests;

/// <summary>Stands in for the web: tests say what a URL serves, and assert on
/// everything around the fetch — the node, the source URL, the versions, the search
/// text — which is the part Gatherum owns.</summary>
public sealed class FakePageArchiver : IPageArchiver
{
    public Dictionary<string, ArchivedPage> Pages { get; } = new();
    public string? Refusal { get; set; }
    public int Fetches { get; private set; }

    public Task<ArchivedPage> ArchiveAsync(Uri url, CancellationToken cancellationToken = default)
    {
        Fetches++;
        if (Refusal is not null)
            throw new PageArchiveException(Refusal);
        return Pages.TryGetValue(url.AbsoluteUri, out var page)
            ? Task.FromResult(page)
            : throw new PageArchiveException($"{url} answered 404 Not Found.");
    }
}
