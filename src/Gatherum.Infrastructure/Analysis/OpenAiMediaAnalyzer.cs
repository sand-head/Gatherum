using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatherum.Infrastructure.Analysis;

/// <summary>Gives an image, a recording, or a video the words it never had: what is
/// written in it, what is said in it, and what it is about. One any-to-any model behind
/// an OpenAI-compatible endpoint answers all three, so a photo of a whiteboard becomes
/// findable by the writing on it and a screen recording by the sentence somebody said
/// forty minutes in.</summary>
public class OpenAiMediaAnalyzer(HttpClient http, IOptions<GatherumOptions> options,
    ILogger<OpenAiMediaAnalyzer> logger) : IMediaAnalyzer
{
    private const string OcrPrompt =
        "Transcribe every word visible in this image exactly as written, preserving " +
        "reading order and line breaks. Do not describe the image, do not add commentary, " +
        "and do not guess at text that is illegible. Reply with the transcribed text alone. " +
        "If the image contains no legible text at all, reply with exactly NONE.";

    private const string ImageSummaryPrompt =
        "Describe this image in one short paragraph for someone searching a knowledge base " +
        "who cannot see it. Say what it shows — subject, setting, and anything distinctive " +
        "worth searching for. Reply with the description alone.";

    private const string SpeechPrompt =
        "Transcribe all speech in this recording verbatim. Do not summarize, translate, or " +
        "add commentary. Reply with the transcript alone. If there is no intelligible " +
        "speech, reply with exactly NONE.";

    private const string SoundPrompt =
        "Describe what can be heard in this recording in one short paragraph, for someone " +
        "searching a knowledge base who cannot play it. Reply with the description alone.";

    /// <summary>The one answer that means "nothing here", asked for by name so an empty
    /// result is unmistakable — models otherwise volunteer a paragraph explaining that
    /// they found no text, and that paragraph would be indexed as if it were the text.</summary>
    private const string Nothing = "NONE";

    private readonly OpenAiChatClient chat = new(http);

    private AnalysisOptions Options => options.Value.Analysis;

    public bool CanAnalyze(string mediaType, string fileName) =>
        Options.IsConfigured && (IsImage(mediaType) || IsAudio(mediaType) || IsVideo(mediaType));

    public async Task<MediaAnalysis> AnalyzeAsync(MediaSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.SizeBytes > Options.MaxBytes)
            throw new InvalidOperationException(
                $"{source.FileName} is {source.SizeBytes} bytes, past the " +
                $"{Options.MaxBytes}-byte analysis ceiling (Gatherum:Analysis:MaxBytes).");

        if (IsImage(source.MediaType))
            return await AnalyzeImageAsync(source, cancellationToken);
        if (IsAudio(source.MediaType))
            return await AnalyzeAudioAsync(source, cancellationToken);
        return await AnalyzeVideoAsync(source, cancellationToken);
    }

    /// <summary>Two passes, not one: asking for the words and the description together
    /// gets a description with the words paraphrased into it, and the point of the
    /// transcript is that it is exact.</summary>
    private async Task<MediaAnalysis> AnalyzeImageAsync(MediaSource source, CancellationToken ct)
    {
        var image = MediaPart.Image(source.MediaType, await ReadAsync(source, ct));
        var transcript = Words(await chat.CompleteAsync(Options.Model, OcrPrompt, [image], ct));
        var summary = Words(await chat.CompleteAsync(Options.Model, ImageSummaryPrompt, [image], ct));
        return new MediaAnalysis(transcript, summary);
    }

    private async Task<MediaAnalysis> AnalyzeAudioAsync(MediaSource source, CancellationToken ct)
    {
        var audio = MediaPart.Audio(source.MediaType, source.FileName, await ReadAsync(source, ct));
        return await FromSpeechAsync(audio, source.FileName, "recording", ct);
    }

    /// <summary>A video is not one medium but two. ffmpeg splits it into the audio the
    /// model listens to and a few frames it looks at; the summary reads both, so a silent
    /// screencast is still described and a talking head is still described by what was
    /// said rather than by the wall behind it.</summary>
    private async Task<MediaAnalysis> AnalyzeVideoAsync(MediaSource source, CancellationToken ct)
    {
        var ffmpeg = new Ffmpeg(Options.FfmpegPath);
        var path = Path.Combine(Path.GetTempPath(), $"gatherum-video-{Guid.NewGuid():N}");
        try
        {
            await using (var input = await source.OpenAsync(ct))
            await using (var file = File.Create(path))
            {
                await input.CopyToAsync(file, ct);
            }

            var transcript = "";
            try
            {
                var track = await ffmpeg.ExtractAudioAsync(path, ct);
                transcript = Words(await chat.CompleteAsync(Options.AudioModelOrDefault,
                    SpeechPrompt, [MediaPart.Audio("audio/wav", "audio.wav", track)], ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A video with no audio stream is ordinary, and so is a model that only
                // sees. Either way the frames still have something to say.
                logger.LogInformation(ex, "No transcript for {FileName}; describing frames only",
                    source.FileName);
            }

            var frames = await ffmpeg.SampleFramesAsync(path, Options.VideoFrames, ct);
            var summary = await DescribeVideoAsync(frames, transcript, ct);
            return new MediaAnalysis(transcript, summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<string> DescribeVideoAsync(IReadOnlyList<byte[]> frames, string transcript,
        CancellationToken ct)
    {
        var parts = frames.Select(f => MediaPart.Image("image/jpeg", f)).ToList();
        if (parts.Count == 0 && transcript.Length == 0)
            return "";

        var prompt = parts.Count > 0
            ? "These frames are sampled in order from a single video. Describe the video in " +
              "one short paragraph for someone searching a knowledge base who cannot watch it. " +
              "Reply with the description alone."
            : "Summarize this video for a knowledge base search index, in one short paragraph. " +
              "Reply with the summary alone.";
        if (transcript.Length > 0)
            prompt += $"\n\nWhat is said in it:\n{Excerpt(transcript)}";

        return Words(await chat.CompleteAsync(Options.Model, prompt, parts, ct));
    }

    /// <summary>Speech first, and the summary from the transcript as plain text rather
    /// than by playing the audio a second time — cheaper, and a model summarizes words it
    /// can read better than words it has to hear again. Silence falls back to describing
    /// the sound itself, which is the only thing left to index.</summary>
    private async Task<MediaAnalysis> FromSpeechAsync(MediaPart audio, string fileName, string kind,
        CancellationToken ct)
    {
        var transcript = Words(await chat.CompleteAsync(Options.AudioModelOrDefault, SpeechPrompt,
            [audio], ct));
        if (transcript.Length == 0)
        {
            logger.LogInformation("No speech found in {FileName}; describing the sound instead",
                fileName);
            return new MediaAnalysis("",
                Words(await chat.CompleteAsync(Options.AudioModelOrDefault, SoundPrompt, [audio], ct)));
        }

        var summary = Words(await chat.CompleteAsync(Options.Model,
            $"Summarize this {kind} for a knowledge base search index, in one short paragraph. " +
            $"Reply with the summary alone.\n\n{Excerpt(transcript)}", [], ct));
        return new MediaAnalysis(transcript, summary);
    }

    private async Task<byte[]> ReadAsync(MediaSource source, CancellationToken ct)
    {
        await using var stream = await source.OpenAsync(ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>A model's answer, or nothing. Both an empty reply and the agreed
    /// <see cref="Nothing"/> mean the medium had none of what we asked for.</summary>
    private static string Words(string answer)
    {
        var trimmed = answer.Trim();
        return trimmed.Equals(Nothing, StringComparison.OrdinalIgnoreCase) ? "" : trimmed;
    }

    /// <summary>Enough transcript to summarize from. A three-hour recording would
    /// otherwise blow the context window of the model being asked about it.</summary>
    private static string Excerpt(string transcript) =>
        transcript.Length <= 24_000 ? transcript : transcript[..24_000] + "…";

    private static bool IsImage(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
        !mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudio(string mediaType) =>
        mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    private static bool IsVideo(string mediaType) =>
        mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}
