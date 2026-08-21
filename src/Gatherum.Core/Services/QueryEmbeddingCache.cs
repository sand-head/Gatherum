using System.Collections.Concurrent;
using Pgvector;

namespace Gatherum.Core.Services;

/// <summary>Remembers what recent search boxes embedded to. The palette searches as
/// someone types, so the same prefixes come back keystroke after keystroke and a
/// backspace asks for one already answered. Bounded and unordered: this is a cache, and
/// forgetting the wrong entry costs one model call.</summary>
public class QueryEmbeddingCache
{
    private const int Capacity = 256;

    private readonly ConcurrentDictionary<(string Model, string Query), Vector> entries = new();

    public bool TryGet(string model, string query, out Vector? vector)
    {
        var found = entries.TryGetValue((model, query), out var stored);
        vector = stored;
        return found;
    }

    public void Set(string model, string query, Vector vector)
    {
        if (entries.Count >= Capacity)
            entries.Clear();
        entries[(model, query)] = vector;
    }
}
