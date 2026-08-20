using System.Text;
using System.Text.Json.Nodes;

namespace Gatherum.Infrastructure.Analysis;

/// <summary>The one wire shape this speaks: OpenAI's <c>/chat/completions</c>, which is
/// what llama.cpp's server and every other local runner already answer. Multimodal input
/// rides in the content-part array, so a single endpoint covers reading words off an
/// image, hearing speech in a recording, and writing the summary of either.</summary>
public class OpenAiChatClient(HttpClient http)
{
    public async Task<string> CompleteAsync(string model, string prompt,
        IReadOnlyList<MediaPart> media, CancellationToken cancellationToken = default)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt });
        foreach (var part in media)
            content.Add(part.ToJson());

        var request = new JsonObject
        {
            ["model"] = model,
            ["stream"] = false,
            // Low but not zero: a transcript should read back what is there, not
            // improvise around it, and a summary still needs to form a sentence.
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "user", ["content"] = content }),
        };

        using var body = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("chat/completions", body, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The analysis endpoint answered {(int)response.StatusCode}: {Excerpt(text)}");

        return JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?
            .GetValue<string>()?.Trim() ?? "";
    }

    private static string Excerpt(string body) =>
        body.Length <= 400 ? body : body[..400] + "…";
}

/// <summary>One non-text piece of a prompt, inlined as base64. Local runners have no
/// URL of ours they could fetch back, and inlining is what keeps the bytes on the same
/// machine they were uploaded to.</summary>
public record MediaPart(bool IsAudio, string MediaType, string FileName, byte[] Bytes)
{
    public static MediaPart Image(string mediaType, byte[] bytes) =>
        new(false, mediaType, "", bytes);

    public static MediaPart Audio(string mediaType, string fileName, byte[] bytes) =>
        new(true, mediaType, fileName, bytes);

    public JsonNode ToJson() => IsAudio
        ? new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject
            {
                ["data"] = Convert.ToBase64String(Bytes),
                ["format"] = AudioFormat(MediaType, FileName),
            },
        }
        : new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject
            {
                ["url"] = $"data:{MediaType};base64,{Convert.ToBase64String(Bytes)}",
            },
        };

    /// <summary>The bare container word the API wants ("mp3", "wav"), which is not the
    /// media type and not always the extension either.</summary>
    public static string AudioFormat(string mediaType, string fileName) =>
        mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/ogg" or "audio/vorbis" => "ogg",
            "audio/opus" => "opus",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "m4a",
            "audio/aac" => "aac",
            "audio/webm" => "webm",
            _ => Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant() is { Length: > 0 } ext
                ? ext
                : "wav",
        };
}
