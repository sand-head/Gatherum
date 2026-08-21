using Gatherum.Core;
using Gatherum.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatherum.Tests;

/// <summary>The packaged model itself, exercised as it ships. Everything else about
/// semantic search is tested against FakeEmbedder, because a real model's answers are
/// approximate and assertions about ranking made against one are flaky by construction —
/// so what is asserted here is only what this model must never get wrong: its shape, its
/// determinism, and that it can tell two subjects apart at all.</summary>
public class LocalEmbedderTests
{
    private static LocalEmbedder Create() =>
        new(Options.Create(new GatherumOptions()), NullLogger<LocalEmbedder>.Instance);

    /// <summary>Cosine distance, which is what the vectors are normalized for and what
    /// pgvector orders by.</summary>
    private static double Distance(float[] a, float[] b) =>
        1 - a.Zip(b).Sum(pair => (double)pair.First * pair.Second);

    [Fact]
    public void The_model_ships_with_the_build() =>
        Assert.True(LocalEmbedder.IsAvailable(new EmbeddingOptions().ModelPath),
            "the build's FetchEmbeddingModel target should have put the model in the output");

    [Fact]
    public async Task Vectors_are_the_width_the_defaults_promise()
    {
        using var embedder = Create();

        var vectors = await embedder.EmbedAsync(["a passage about quadlets"]);

        Assert.Equal(LocalEmbedder.ModelDimensions, vectors[0].Length);
        Assert.Equal(new EmbeddingOptions().Dimensions, vectors[0].Length);
    }

    [Fact]
    public async Task Vectors_come_out_normalized()
    {
        using var embedder = Create();

        var vector = (await embedder.EmbedAsync(["rootless podman quadlets"]))[0];

        var norm = Math.Sqrt(vector.Sum(component => (double)component * component));
        Assert.Equal(1.0, norm, 3);
    }

    [Fact]
    public async Task The_same_text_embeds_the_same_way_twice()
    {
        using var embedder = Create();

        var first = (await embedder.EmbedAsync(["the fans ran all afternoon"]))[0];
        var second = (await embedder.EmbedAsync(["the fans ran all afternoon"]))[0];

        Assert.Equal(first, second);
    }

    /// <summary>A vector must be a function of its text and nothing else. This model is
    /// quantized, and quantized activations are scaled across whatever tensor they arrive
    /// in — so embedding a batch as one tensor would make a passage's vector depend on
    /// its neighbours, and a query (always alone) systematically unlike the passages it
    /// is compared against. LocalEmbedder embeds one at a time to prevent exactly
    /// this.</summary>
    [Fact]
    public async Task A_passage_embeds_the_same_alone_as_in_a_batch()
    {
        using var embedder = Create();
        var texts = new[]
        {
            "short one",
            "a considerably longer passage about rootless podman quadlets, restarting " +
            "cleanly after a reboot once lingering is enabled for the user account",
            "middling length passage here",
        };

        var batched = await embedder.EmbedAsync(texts);

        for (var i = 0; i < texts.Length; i++)
            Assert.Equal((await embedder.EmbedAsync([texts[i]]))[0], batched[i]);
    }

    [Fact]
    public async Task It_can_tell_two_subjects_apart()
    {
        using var embedder = Create();
        var vectors = await embedder.EmbedAsync([
            "The overheating started after the third drive went into the rack.",
            "Why does the server closet get so hot in the afternoon?",
            "Quarterly billing reconciliation for the accountant.",
        ]);

        var related = Distance(vectors[0], vectors[1]);
        var unrelated = Distance(vectors[0], vectors[2]);

        Assert.True(related < unrelated,
            $"kin measured {related:F3} apart and strangers {unrelated:F3}");
        // The cutoff the defaults ship with has to fall between the two, or semantic
        // search either answers everything or nothing.
        var cutoff = new EmbeddingOptions().MaxDistance;
        Assert.InRange(cutoff, related, unrelated);
    }

    [Fact]
    public async Task Text_past_the_model_s_window_is_truncated_rather_than_refused()
    {
        using var embedder = Create();
        var long_ = string.Join(' ', Enumerable.Repeat("padding words that go on and on", 500));

        var vectors = await embedder.EmbedAsync([long_]);

        Assert.Equal(LocalEmbedder.ModelDimensions, vectors[0].Length);
    }
}
