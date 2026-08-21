namespace Gatherum.Core.Services;

/// <summary>Cuts a node's text into passages small enough for an embedding model to hold
/// in one thought. Cuts on the blank lines the writer already put there, so a passage is
/// a run of whole paragraphs rather than an arbitrary window, and carries the tail of
/// each passage into the next so a sentence split across the seam is still whole
/// somewhere.</summary>
public static class TextChunker
{
    /// <summary>How much of the previous passage rides along at the head of the next.
    /// Small enough not to blur what a passage is about, large enough to catch the
    /// sentence that straddles the cut.</summary>
    public const int OverlapChars = 150;

    public static List<string> Chunk(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        // Packing stops short of the ceiling because the overlap is added afterwards,
        // which is what keeps every chunk inside the budget the model was promised.
        var budget = Math.Max(OverlapChars * 2, maxChars - OverlapChars);

        var packed = new List<string>();
        var current = new List<string>();
        var length = 0;
        foreach (var block in Blocks(text, budget))
        {
            if (length > 0 && length + block.Length + 2 > budget)
            {
                packed.Add(string.Join("\n\n", current));
                current.Clear();
                length = 0;
            }
            current.Add(block);
            length += block.Length + 2;
        }
        if (current.Count > 0)
            packed.Add(string.Join("\n\n", current));

        var chunks = new List<string>(packed.Count);
        for (var i = 0; i < packed.Count; i++)
            chunks.Add(i == 0 ? packed[i] : Tail(packed[i - 1]) + "\n\n" + packed[i]);
        return chunks;
    }

    /// <summary>Paragraphs, with any single paragraph too long to be one broken at the
    /// last word boundary that fits. A run-on transcript with no blank lines in it is
    /// one paragraph, and this is what keeps that case from becoming one huge chunk.</summary>
    private static IEnumerable<string> Blocks(string text, int budget)
    {
        var paragraphs = text.ReplaceLineEndings("\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var paragraph in paragraphs)
        {
            var rest = paragraph;
            while (rest.Length > budget)
            {
                var cut = rest.LastIndexOfAny([' ', '\n'], budget);
                if (cut <= 0)
                    cut = budget;
                yield return rest[..cut].Trim();
                rest = rest[cut..].TrimStart();
            }
            if (rest.Length > 0)
                yield return rest;
        }
    }

    private static string Tail(string chunk)
    {
        if (chunk.Length <= OverlapChars)
            return chunk;
        var tail = chunk[^OverlapChars..];
        var boundary = tail.IndexOfAny([' ', '\n']);
        return boundary < 0 ? tail : tail[(boundary + 1)..];
    }
}
