using System.Security.Cryptography;
using System.Text;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>Keeps every node's vectors abreast of its text, and answers the two nearness
/// questions the app asks: what is near this search box, and what is near this node.
/// With no embedder registered every method here is a no-op that costs one null check,
/// which is what lets search go on being exactly the full-text search it was.</summary>
public class EmbeddingService(
    GatherumDbContext db,
    IEnumerable<IEmbedder> embedders,
    QueryEmbeddingCache cache,
    IOptions<GatherumOptions> options,
    ILogger<EmbeddingService> logger)
{
    private readonly IEmbedder? embedder = embedders.FirstOrDefault();
    private readonly EmbeddingOptions settings = options.Value.Embedding;

    public bool IsEnabled => embedder is not null;

    /// <summary>Nodes whose text has moved on from what was embedded of it — the whole
    /// of the worker's to-do list, and a plain index scan. Nothing has to remember to
    /// put a node here: the fingerprint it is compared against is computed by the
    /// database from the columns themselves.</summary>
    public Task<List<StaleNode>> StaleNodesAsync(int limit, CancellationToken ct = default) =>
        db.Nodes
            .Where(n => n.EmbeddedFingerprint != n.TextFingerprint)
            .OrderBy(n => n.UpdatedAt)
            .Take(limit)
            .Select(n => new StaleNode(n.Id, n.TextFingerprint))
            .ToListAsync(ct);

    public Task<int> StaleCountAsync(CancellationToken ct = default) =>
        db.Nodes.CountAsync(n => n.EmbeddedFingerprint != n.TextFingerprint, ct);

    /// <summary>Re-embeds one node: cut its text into passages, look up the ones already
    /// paid for, ask the model about the rest, and swap the node's vectors for the new
    /// set. If the text changes underneath while the model is thinking, the node is left
    /// marked stale rather than stamped with a fingerprint it no longer has — the next
    /// sweep picks it up, and no edit is ever silently unindexed.</summary>
    public async Task EmbedNodeAsync(Guid nodeId, CancellationToken ct = default)
    {
        if (embedder is null)
            return;

        var node = await db.Nodes.AsNoTracking()
            .Where(n => n.Id == nodeId)
            .Select(n => new { n.Id, n.Title, n.SearchText, n.TextFingerprint })
            .FirstOrDefaultAsync(ct);
        if (node is null)
            return;

        var passages = Passages(node.Title, node.SearchText);
        var texts = passages.Select(passage => Embedded(node.Title, passage)).ToList();
        var hashes = texts.Select(Fingerprint).ToList();

        var known = await db.NodeEmbeddings
            .Where(e => hashes.Contains(e.Hash) && e.Model == embedder.Model)
            .Select(e => new { e.Hash, e.Embedding })
            .ToListAsync(ct);
        var reused = known
            .GroupBy(e => e.Hash)
            .ToDictionary(group => group.Key, group => group.First().Embedding);

        var missing = hashes.Where(hash => !reused.ContainsKey(hash)).Distinct().ToList();
        if (missing.Count > 0)
        {
            var byHash = texts.Zip(hashes).DistinctBy(pair => pair.Second)
                .ToDictionary(pair => pair.Second, pair => pair.First);
            foreach (var batch in missing.Chunk(Math.Max(1, settings.BatchSize)))
            {
                var vectors = await embedder.EmbedAsync([.. batch.Select(hash => byHash[hash])], ct);
                if (vectors.Count != batch.Length)
                    throw new InvalidOperationException(
                        $"The embedding endpoint answered with {vectors.Count} vectors for " +
                        $"{batch.Length} passages.");
                foreach (var (hash, vector) in batch.Zip(vectors))
                    reused[hash] = Checked(vector);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.NodeEmbeddings.Where(e => e.NodeId == nodeId).ExecuteDeleteAsync(ct);
        db.NodeEmbeddings.AddRange(passages.Select((passage, ordinal) => new NodeEmbedding
        {
            Id = Guid.NewGuid(),
            NodeId = nodeId,
            Ordinal = ordinal,
            Text = passage,
            Hash = hashes[ordinal],
            Embedding = reused[hashes[ordinal]],
            Model = embedder.Model,
        }));
        await db.SaveChangesAsync(ct);
        await db.Nodes
            .Where(n => n.Id == nodeId && n.TextFingerprint == node.TextFingerprint)
            .ExecuteUpdateAsync(
                set => set.SetProperty(n => n.EmbeddedFingerprint, node.TextFingerprint), ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The search box as a vector, or null when there is no model, the model
    /// says no, or it takes longer than a person will wait — every one of which means
    /// "search with the other half", never "fail the search".</summary>
    public async Task<Vector?> EmbedQueryAsync(string query, CancellationToken ct = default)
    {
        if (embedder is null || string.IsNullOrWhiteSpace(query))
            return null;
        if (cache.TryGet(embedder.Model, query, out var cached))
            return cached;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(settings.QueryTimeoutMs));
        try
        {
            var vectors = await embedder.EmbedAsync([query], deadline.Token);
            var vector = Checked(vectors[0]);
            cache.Set(embedder.Model, query, vector);
            return vector;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not embed the query; answering from full-text search alone");
            return null;
        }
    }

    /// <summary>The passages nearest a vector, nearest first, drawn only from nodes the
    /// query hands in — so a private subtree is filtered in the database and not after
    /// the fact — and only those near enough to be an answer at all.</summary>
    public async Task<List<VectorHit>> NearestAsync(IQueryable<Node> visible, Vector query,
        int limit, CancellationToken ct = default)
    {
        var ids = visible.Select(n => n.Id);
        var hits = await db.NodeEmbeddings
            .Where(e => ids.Contains(e.NodeId))
            .Where(e => e.Embedding.CosineDistance(query) <= settings.MaxDistance)
            .OrderBy(e => e.Embedding.CosineDistance(query))
            .Take(limit)
            .Select(e => new { e.NodeId, e.Text, Distance = e.Embedding.CosineDistance(query) })
            .ToListAsync(ct);
        return hits.Select(hit => new VectorHit(hit.NodeId, hit.Text, hit.Distance)).ToList();
    }

    /// <summary>Where a node sits in the vector space: the mean of its passages. A
    /// centroid is a blunt instrument for retrieval, but "what else is about roughly
    /// this?" is exactly the blunt question, and it is one vector instead of forty.</summary>
    public async Task<Vector?> CentroidAsync(Guid nodeId, CancellationToken ct = default)
    {
        if (embedder is null)
            return null;
        var vectors = await db.NodeEmbeddings
            .Where(e => e.NodeId == nodeId)
            .Select(e => e.Embedding)
            .ToListAsync(ct);
        if (vectors.Count == 0)
            return null;

        var mean = new float[vectors[0].Memory.Length];
        foreach (var vector in vectors)
        {
            var span = vector.Memory.Span;
            for (var i = 0; i < mean.Length && i < span.Length; i++)
                mean[i] += span[i];
        }
        for (var i = 0; i < mean.Length; i++)
            mean[i] /= vectors.Count;
        return new Vector(mean);
    }

    /// <summary>Title and passage go to the model together: a paragraph halfway down a
    /// page rarely repeats what the page is called, and without it "the fan is loud"
    /// belongs to no subject at all.</summary>
    private static string Embedded(string title, string passage) =>
        title.Length > 0 ? $"{title}\n\n{passage}" : passage;

    private List<string> Passages(string title, string searchText)
    {
        var passages = TextChunker.Chunk(searchText, settings.MaxChunkChars);
        if (passages.Count > settings.MaxChunksPerNode)
        {
            logger.LogWarning(
                "Embedding the first {Kept} of {Total} passages; the rest stays findable " +
                "by full-text search only", settings.MaxChunksPerNode, passages.Count);
            passages = passages[..settings.MaxChunksPerNode];
        }
        // A node with a title and nothing else is still about something.
        if (passages.Count == 0 && title.Length > 0)
            passages.Add(title);
        return passages;
    }

    private Vector Checked(float[] vector) =>
        vector.Length == settings.Dimensions
            ? new Vector(vector)
            : throw new InvalidOperationException(
                $"{embedder!.Model} returned {vector.Length}-dimensional vectors, but " +
                $"Gatherum__Embedding__Dimensions says {settings.Dimensions}.");

    private static string Fingerprint(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>A node waiting to be embedded, and the fingerprint it was waiting with — so
/// a worker that has already broken its teeth on one can tell a retry of the same text
/// apart from a genuine edit.</summary>
public record StaleNode(Guid Id, string Fingerprint);

/// <summary>One passage that came back near a vector. <paramref name="Distance"/> is a
/// cosine distance — zero is identical, so smaller is nearer.</summary>
public record VectorHit(Guid NodeId, string Text, double Distance);
