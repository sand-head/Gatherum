namespace Gatherum.Core.Services;

/// <summary>Merges two ranked lists that disagree about what a score means. Postgres
/// hands back <c>ts_rank</c> and pgvector hands back a cosine distance; the two are on
/// no common scale, and any attempt to normalize them into one is a guess that quietly
/// decides how much a lexical hit is worth. Reciprocal rank fusion reads only the
/// positions, so a result near the top of either list places well and a result near the
/// top of both wins outright.</summary>
public static class RankFusion
{
    /// <summary>The damping constant from the original RRF paper. Large enough that the
    /// gap between first and second place is not a cliff, so agreement between the two
    /// lists matters more than either one's exact ordering.</summary>
    private const double Damping = 60;

    public static List<T> Fuse<T>(IReadOnlyList<IReadOnlyList<T>> rankings, int limit)
        where T : notnull
    {
        var scores = new Dictionary<T, double>();
        var firstSeen = new Dictionary<T, int>();
        var order = 0;
        foreach (var ranking in rankings)
            for (var rank = 0; rank < ranking.Count; rank++)
            {
                var item = ranking[rank];
                scores[item] = scores.GetValueOrDefault(item) + 1 / (Damping + rank + 1);
                if (!firstSeen.ContainsKey(item))
                    firstSeen[item] = order++;
            }

        return scores
            .OrderByDescending(entry => entry.Value)
            // Ties break toward whichever list reached the item first, so fusion is a
            // pure function of its input and a search run twice reads the same.
            .ThenBy(entry => firstSeen[entry.Key])
            .Take(limit)
            .Select(entry => entry.Key)
            .ToList();
    }
}
