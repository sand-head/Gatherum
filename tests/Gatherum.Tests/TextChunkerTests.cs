using Gatherum.Core.Services;

namespace Gatherum.Tests;

public class TextChunkerTests
{
    [Fact]
    public void Short_text_is_one_passage()
    {
        var chunks = TextChunker.Chunk("A short note about quadlets.", 1200);

        Assert.Equal(["A short note about quadlets."], chunks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Text_with_nothing_in_it_makes_no_passages(string text) =>
        Assert.Empty(TextChunker.Chunk(text, 1200));

    [Fact]
    public void Passages_stay_within_the_budget()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 40)
            .Select(i => $"Paragraph {i}: " + string.Join(' ', Enumerable.Repeat("word", 30))));

        var chunks = TextChunker.Chunk(text, 600);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 600,
            $"a passage ran to {chunk.Length} characters"));
    }

    [Fact]
    public void A_paragraph_too_long_to_be_a_passage_is_broken_on_a_word_boundary()
    {
        var text = string.Join(' ', Enumerable.Repeat("indivisible", 200));

        var chunks = TextChunker.Chunk(text, 300);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.DoesNotContain("indivis ", chunk));
    }

    [Fact]
    public void Every_word_survives_the_cutting()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 30)
            .Select(i => $"Paragraph {i} carries the marker word{i} in it."));

        var chunks = TextChunker.Chunk(text, 400);

        for (var i = 0; i < 30; i++)
            Assert.Contains(chunks, chunk => chunk.Contains($"word{i} "));
    }

    [Fact]
    public void Each_passage_after_the_first_carries_the_tail_of_the_one_before()
    {
        var text = string.Join("\n\n", Enumerable.Range(0, 12)
            .Select(i => $"Paragraph {i}: " + string.Join(' ', Enumerable.Repeat("word", 25))));

        var chunks = TextChunker.Chunk(text, 500);

        Assert.True(chunks.Count > 1);
        for (var i = 1; i < chunks.Count; i++)
        {
            var overlap = chunks[i][..TextChunker.OverlapChars];
            Assert.Contains(overlap.Trim(), chunks[i - 1]);
        }
    }
}
