using Gatherum.Core.Abstractions;

namespace Gatherum.Tests;

/// <summary>An embedder with no model behind it, and the only kind that can prove
/// anything about semantic search in a test: a real model's answers are approximate, and
/// an assertion about ranking made against one is a coin toss dressed as a test.
///
/// It behaves like a very small model. Words it has never been told about land in
/// dimensions picked by their own hash, so texts sharing words come out near each other
/// and texts sharing none come out far apart. On top of that a fixture can declare a
/// *subject* — a set of words that mean the same thing — which claims a dimension of its
/// own and outweighs the rest, so a page written in one vocabulary and a search written
/// in another land together. That is the case semantic search exists for, and the only
/// way to assert on it deterministically.</summary>
public sealed class FakeEmbedder : IEmbedder
{
    /// <summary>Dimensions kept for declared subjects; the rest carry hashed words.</summary>
    private const int SubjectDimensions = 8;

    /// <summary>How much louder a declared subject is than an incidental shared word.</summary>
    private const float SubjectWeight = 4;

    private static readonly char[] Separators =
        [' ', '\n', '\r', '\t', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '/', '-'];

    public List<HashSet<string>> Subjects { get; } = [];

    public int Dimensions { get; init; } = PostgresFixture.EmbeddingDimensions;
    public string Model { get; set; } = "fake-embed";
    public Exception? Throws { get; set; }
    public TimeSpan Delay { get; set; }
    public List<string> Embedded { get; } = [];

    /// <summary>Declares that these words all mean one thing.</summary>
    public void Means(params string[] words) => Subjects.Add([.. words]);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
        if (Throws is not null)
            throw Throws;

        Embedded.AddRange(texts);
        return [.. texts.Select(Vector)];
    }

    private float[] Vector(string text)
    {
        var words = text.ToLowerInvariant()
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        var vector = new float[Dimensions];
        for (var i = 0; i < Subjects.Count && i < SubjectDimensions; i++)
            if (Subjects[i].Overlaps(words))
                vector[i] = SubjectWeight;

        var spread = Dimensions - SubjectDimensions;
        foreach (var word in words)
            vector[SubjectDimensions + (int)(Hash(word) % (uint)spread)] += 1;

        // An empty text would otherwise embed to all zeroes, and the cosine distance to a
        // zero vector is not a number — which surfaces as NaN ordering, not as "unrelated".
        if (vector.All(component => component == 0))
            vector[^1] = 1;
        return vector;
    }

    /// <summary>FNV-1a, spelled out rather than taken from string.GetHashCode, which is
    /// randomized per process and would make a ranking test pass or fail by run.</summary>
    private static uint Hash(string word)
    {
        var hash = 2166136261u;
        foreach (var character in word)
            hash = (hash ^ character) * 16777619u;
        return hash;
    }
}
