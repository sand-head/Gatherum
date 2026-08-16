using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

public class SearchService(GatherumDbContext db, INodeAuthorizer authorizer)
{
    public async Task<List<SearchResult>> SearchAsync(Guid userId, string query,
        NodeKind? kind = null, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        limit = Math.Clamp(limit, 1, 100);

        var matches = await authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => kind == null || n.Kind == kind)
            .Where(n => n.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("english", query)))
            .OrderByDescending(n =>
                n.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("english", query)))
            .Take(limit)
            .Select(n => new { n.Id, n.Title, n.Kind, n.SearchText })
            .ToListAsync(ct);

        return matches
            .Select(n => new SearchResult(n.Id, n.Title, n.Kind, Snippet(n.SearchText, query)))
            .ToList();
    }

    /// <summary>A window of search text around the first query term. Postgres could do
    /// this with ts_headline, but a C# window keeps the query simple and provider-mappable.</summary>
    internal static string Snippet(string text, string query, int radius = 90)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hit = terms
            .Select(t => flattened.IndexOf(t, StringComparison.OrdinalIgnoreCase))
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (hit < 0)
            return flattened.Length <= radius * 2 ? flattened : flattened[..(radius * 2)] + "…";

        var start = Math.Max(0, hit - radius);
        var end = Math.Min(flattened.Length, hit + radius);
        return (start > 0 ? "…" : "") + flattened[start..end] + (end < flattened.Length ? "…" : "");
    }
}

public record SearchResult(Guid Id, string Title, NodeKind Kind, string Snippet);
