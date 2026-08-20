using System.Text;
using System.Text.Json.Nodes;
using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatherum.Infrastructure.Embedding;

/// <summary>The same wire shape the analyzer speaks, one path over: OpenAI's
/// <c>/embeddings</c>, which llama.cpp answers when started with <c>--embeddings</c>.
/// Text only — nothing here inlines bytes, so unlike analysis this never sends a file
/// anywhere, only the words already extracted from one.</summary>
public class OpenAiEmbedder(HttpClient http, IOptions<GatherumOptions> options) : IEmbedder
{
    private readonly EmbeddingOptions settings = options.Value.Embedding;

    public string Model => settings.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
            return [];

        var request = new JsonObject
        {
            ["model"] = settings.Model,
            ["input"] = new JsonArray([.. texts.Select(text => JsonValue.Create(text))]),
            // Some servers answer base64 unless told otherwise, and a silently base64'd
            // vector parses as an empty one.
            ["encoding_format"] = "float",
        };

        using var body = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("embeddings", body, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The embedding endpoint answered {(int)response.StatusCode}: {Excerpt(text)}");

        var data = JsonNode.Parse(text)?["data"]?.AsArray()
            ?? throw new HttpRequestException(
                $"The embedding endpoint answered without a data array: {Excerpt(text)}");

        // The API promises an index per row but not an order, and a batch reassembled in
        // the wrong order attaches every vector to the wrong passage — silently.
        return data
            .OrderBy(row => row?["index"]?.GetValue<int>() ?? 0)
            .Select(row => row?["embedding"]?.AsArray()
                    .Select(value => value?.GetValue<float>() ?? 0f).ToArray()
                ?? throw new HttpRequestException(
                    $"The embedding endpoint answered with a row carrying no embedding: {Excerpt(text)}"))
            .ToList();
    }

    private static string Excerpt(string body) =>
        body.Length <= 400 ? body : body[..400] + "…";
}
