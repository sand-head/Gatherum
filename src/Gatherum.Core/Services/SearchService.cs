using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>Which halves of the search to run. <see cref="Hybrid"/> is the answer almost
/// always: the two halves are good at opposite things — one finds the phrase somebody
/// remembers word for word, the other finds the page that never uses their words — and
/// neither is a replacement for the other. The other two exist to be able to ask for one
/// half deliberately, and because a script wanting a literal match should be able to say
/// so.</summary>
public enum SearchMode
{
    Hybrid,
    Text,
    Semantic,
}

public class SearchService(
    GatherumDbContext db,
    INodeAuthorizer authorizer,
    EmbeddingService embeddings)
{
    public async Task<List<SearchResult>> SearchAsync(Guid? userId, string query,
        NodeKind? kind = null, int limit = 20, SearchMode mode = SearchMode.Hybrid,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        limit = Math.Clamp(limit, 1, 100);
        // Both halves look deeper than the caller asked, because fusion only has
        // something to agree about where the two lists overlap.
        var depth = Math.Clamp(limit * 4, limit, 200);

        var visible = authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => kind == null ||
                (kind == NodeKind.Page
                    ? n.MediaType == MediaTypes.Markdown
                    : n.MediaType != MediaTypes.Markdown));

        var lexical = mode == SearchMode.Semantic
            ? []
            : await LexicalAsync(visible, query, depth, ct);
        var semantic = mode == SearchMode.Text
            ? []
            : await SemanticAsync(visible, query, depth, ct);

        // Asking for a half that isn't there answers with the half that is: a search box
        // returning nothing because a model is unconfigured or asleep is a broken search,
        // not an honest one.
        if (semantic.Count == 0 && lexical.Count == 0 && mode == SearchMode.Semantic)
            lexical = await LexicalAsync(visible, query, depth, ct);

        var ranked = RankFusion.Fuse(
            [[.. lexical.Select(hit => hit.Id)], [.. semantic.Select(hit => hit.NodeId)]], limit);
        if (ranked.Count == 0)
            return [];

        var passages = semantic.ToDictionary(hit => hit.NodeId, hit => hit.Passage);
        var found = lexical.ToDictionary(hit => hit.Id);
        var lexicalIds = found.Keys.ToHashSet();
        var missing = ranked.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var extra = await db.Nodes
                .Where(n => missing.Contains(n.Id))
                .Select(n => new LexicalHit(n.Id, n.Title, n.MediaType, n.SearchText))
                .ToListAsync(ct);
            foreach (var hit in extra)
                found[hit.Id] = hit;
        }

        return ranked
            .Where(found.ContainsKey)
            .Select(id => new SearchResult(id, found[id].Title,
                found[id].MediaType == MediaTypes.Markdown ? NodeKind.Page : NodeKind.File,
                // A node the full-text half found contains the words that were typed, so
                // its snippet is windowed around them. One only the vector half found may
                // contain none of them, and the passage that matched says far more about
                // why it is here than the top of its search text would.
                Snippet(passages.TryGetValue(id, out var passage) && !lexicalIds.Contains(id)
                    ? passage
                    : found[id].SearchText, query)))
            .ToList();
    }

    private static async Task<List<LexicalHit>> LexicalAsync(IQueryable<Node> visible, string query,
        int depth, CancellationToken ct) =>
        await visible
            .Where(n => n.SearchVector.Matches(EF.Functions.WebSearchToTsQuery("english", query)))
            .OrderByDescending(n =>
                n.SearchVector.Rank(EF.Functions.WebSearchToTsQuery("english", query)))
            .Take(depth)
            .Select(n => new LexicalHit(n.Id, n.Title, n.MediaType, n.SearchText))
            .ToListAsync(ct);

    /// <summary>Nodes in nearest-first order — the order *is* the ranking, so it is a
    /// list and not a dictionary — each paired with the passage that got it there. A long
    /// page can answer with several passages; only its nearest counts, or one rambling
    /// document would fill the results with itself.</summary>
    private async Task<List<SemanticHit>> SemanticAsync(IQueryable<Node> visible, string query,
        int depth, CancellationToken ct)
    {
        var vector = await embeddings.EmbedQueryAsync(query, ct);
        if (vector is null)
            return [];

        var hits = await embeddings.NearestAsync(visible, vector, depth * 2, ct);
        var seen = new HashSet<Guid>();
        return [.. hits
            .Where(hit => seen.Add(hit.NodeId))
            .Select(hit => new SemanticHit(hit.NodeId, hit.Text))];
    }

    /// <summary>A window of search text around the first query term. Postgres could do
    /// this with ts_headline, but a C# window keeps the query simple and provider-mappable.
    /// Text with none of the query's words in it — the ordinary case for a passage the
    /// vector half found — reads from the top, which is what it means.</summary>
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

    private record LexicalHit(Guid Id, string Title, string MediaType, string SearchText);

    private record SemanticHit(Guid NodeId, string Passage);
}

public record SearchResult(Guid Id, string Title, NodeKind Kind, string Snippet);
