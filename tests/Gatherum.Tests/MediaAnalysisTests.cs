using System.Text;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class MediaAnalysisTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>A real 1×1 PNG, so the metadata extractor has something to read and the
    /// analysis under test is layered on top of what already worked.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private ServiceHarness harness = null!;
    private Guid jess;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        jess = await harness.AddUserAsync("jess");
        harness.Analyzer.Enabled = true;
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task An_uploaded_image_queues_and_becomes_findable_by_what_the_model_read()
    {
        harness.Analyzer.Answer = new MediaAnalysis(
            "SPRINT GOALS: ship the importer",
            "A photograph of a whiteboard in a meeting room.");

        var node = await harness.Files.CreateFileNodeAsync(jess, null, "whiteboard.png",
            "image/png", new MemoryStream(Png));

        var queued = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(MediaAnalysisState.Pending, queued.File!.Current.Analysis);
        Assert.Contains(queued.File.Current.Id, await DrainAsync(harness.AnalysisQueue));

        await harness.AnalyzePendingAsync();

        var analyzed = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(MediaAnalysisState.Complete, analyzed.File!.Current.Analysis);
        Assert.Equal("SPRINT GOALS: ship the importer", analyzed.File.Current.Transcript);

        // Both halves are indexed: the words photographed, and the subject nobody wrote down.
        Assert.Equal(node.Id, Assert.Single(await harness.Search.SearchAsync(jess, "importer")).Id);
        Assert.Equal(node.Id, Assert.Single(await harness.Search.SearchAsync(jess, "whiteboard")).Id);
    }

    [Fact]
    public async Task Audio_and_video_queue_the_same_way_an_image_does()
    {
        harness.Analyzer.Answer = new MediaAnalysis("so the migration ran overnight", "A standup call.");

        var recording = await harness.Files.CreateFileNodeAsync(jess, null, "standup.m4a",
            "audio/mp4", new MemoryStream("fake audio"u8.ToArray()));
        var screencast = await harness.Files.CreateFileNodeAsync(jess, null, "demo.mp4",
            "video/mp4", new MemoryStream("fake video"u8.ToArray()));
        await harness.AnalyzePendingAsync();

        foreach (var id in new[] { recording.Id, screencast.Id })
        {
            var node = await harness.ReloadAsync(jess, id);
            Assert.Equal(MediaAnalysisState.Complete, node.File!.Current.Analysis);
            Assert.Equal("so the migration ran overnight", node.File.Current.Transcript);
        }

        Assert.Equal(2, (await harness.Search.SearchAsync(jess, "overnight")).Count);
    }

    [Fact]
    public async Task Identical_bytes_inherit_the_transcript_already_paid_for()
    {
        await harness.Files.CreateFileNodeAsync(jess, null, "shot.png", "image/png",
            new MemoryStream(Png));
        await harness.AnalyzePendingAsync();

        var again = await harness.Files.CreateFileNodeAsync(jess, null, "same-shot.png",
            "image/png", new MemoryStream(Png));

        var copy = await harness.ReloadAsync(jess, again.Id);
        Assert.Equal(MediaAnalysisState.Complete, copy.File!.Current.Analysis);
        Assert.Equal("transcribed words", copy.File.Current.Transcript);
        Assert.Single(harness.Analyzer.Analyzed);
    }

    [Fact]
    public async Task Restoring_a_version_brings_its_transcript_back_without_asking_again()
    {
        var node = await harness.Files.CreateFileNodeAsync(jess, null, "shot.png", "image/png",
            new MemoryStream(Png));
        await harness.AnalyzePendingAsync();

        harness.Analyzer.Answer = new MediaAnalysis("a different sign", "A different photo.");
        await harness.Files.UploadVersionAsync(jess, node.Id, "shot.png", "image/png",
            new MemoryStream("other bytes"u8.ToArray()));
        await harness.AnalyzePendingAsync();

        await harness.Files.RestoreVersionAsync(jess, node.Id, 1);

        var restored = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(MediaAnalysisState.Complete, restored.File!.Current.Analysis);
        Assert.Equal("transcribed words", restored.File.Current.Transcript);
        Assert.Equal(2, harness.Analyzer.Analyzed.Count);
    }

    [Fact]
    public async Task A_model_that_fails_leaves_the_file_uploaded_and_says_why()
    {
        harness.Analyzer.Throws = new InvalidOperationException("the endpoint answered 503");

        var node = await harness.Files.CreateFileNodeAsync(jess, null, "clip.mp4", "video/mp4",
            new MemoryStream("fake video"u8.ToArray()));
        await harness.Nodes.AddTagAsync(jess, node.Id, "screencast");
        await harness.AnalyzePendingAsync();

        var failed = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(MediaAnalysisState.Failed, failed.File!.Current.Analysis);
        Assert.Contains("503", failed.File.Current.AnalysisError);

        // The bytes were never the model's to lose, and neither was the way in to them.
        var content = await harness.Files.OpenContentAsync(jess, node.Id);
        await using (content.Stream)
            Assert.Equal(10, content.SizeBytes);
        Assert.Equal(node.Id,
            Assert.Single(await harness.Search.SearchAsync(jess, "screencast")).Id);
    }

    [Fact]
    public async Task Nothing_queues_for_files_no_analyzer_claims()
    {
        var notes = await harness.Files.CreateFileNodeAsync(jess, null, "notes.md", "text/markdown",
            new MemoryStream("plain words"u8.ToArray()));
        var drawing = await harness.Files.CreateFileNodeAsync(jess, null, "logo.svg",
            "image/svg+xml", new MemoryStream("<svg/>"u8.ToArray()));

        Assert.Equal(MediaAnalysisState.None,
            (await harness.ReloadAsync(jess, notes.Id)).File!.Current.Analysis);
        Assert.Equal(MediaAnalysisState.None,
            (await harness.ReloadAsync(jess, drawing.Id)).File!.Current.Analysis);
        Assert.Empty(await harness.Files.PendingAnalysisIdsAsync());
    }

    [Fact]
    public async Task With_no_analyzer_configured_an_image_behaves_exactly_as_before()
    {
        harness.Analyzer.Enabled = false;

        var node = await harness.Files.CreateFileNodeAsync(jess, null, "shot.png", "image/png",
            new MemoryStream(Png));

        var stored = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(MediaAnalysisState.None, stored.File!.Current.Analysis);
        Assert.Empty(stored.File.Current.Transcript);
        Assert.Empty(await harness.Files.PendingAnalysisIdsAsync());
    }

    [Fact]
    public async Task Switching_analysis_on_reaches_the_media_already_in_the_tree()
    {
        harness.Analyzer.Enabled = false;
        var old = await harness.Files.CreateFileNodeAsync(jess, null, "old.png", "image/png",
            new MemoryStream(Png));
        await harness.Files.CreateFileNodeAsync(jess, null, "readme.md", "text/markdown",
            new MemoryStream("words"u8.ToArray()));

        harness.Analyzer.Enabled = true;
        var backfilled = await harness.Files.BackfillAnalysisAsync();

        // The photo, and only the photo — a Markdown file was never the model's business.
        var version = Assert.Single(backfilled);
        Assert.Equal((await harness.ReloadAsync(jess, old.Id)).File!.Current.Id, version);

        await harness.AnalyzePendingAsync();
        Assert.Equal(MediaAnalysisState.Complete,
            (await harness.ReloadAsync(jess, old.Id)).File!.Current.Analysis);
    }

    /// <summary>Everything the queue is holding right now. The channel blocks for work
    /// that has not arrived, so the read is bounded by a timeout rather than by count.</summary>
    private static async Task<List<Guid>> DrainAsync(MediaAnalysisQueue queue)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var ids = new List<Guid>();
        try
        {
            await foreach (var id in queue.ReadAllAsync(timeout.Token))
                ids.Add(id);
        }
        catch (OperationCanceledException)
        {
        }
        return ids;
    }
}

public class MediaPartTests
{
    [Theory]
    [InlineData("audio/mpeg", "clip.mp3", "mp3")]
    [InlineData("audio/x-wav", "clip.wav", "wav")]
    [InlineData("audio/mp4", "clip.m4a", "m4a")]
    [InlineData("audio/flac", "clip.flac", "flac")]
    // Unknown type, so the name is the only thing left that knows the container.
    [InlineData("application/octet-stream", "clip.ogg", "ogg")]
    [InlineData("application/octet-stream", "clip", "wav")]
    public void Audio_formats_come_from_the_media_type_then_the_name(
        string mediaType, string fileName, string expected) =>
        Assert.Equal(expected,
            Gatherum.Infrastructure.Analysis.MediaPart.AudioFormat(mediaType, fileName));

    [Fact]
    public void Images_ride_along_as_data_uris_and_audio_as_raw_base64()
    {
        byte[] bytes = [1, 2, 3];
        var image = Gatherum.Infrastructure.Analysis.MediaPart.Image("image/png", bytes).ToJson();
        Assert.Equal("image_url", image["type"]!.GetValue<string>());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(bytes)}",
            image["image_url"]!["url"]!.GetValue<string>());

        var audio = Gatherum.Infrastructure.Analysis.MediaPart
            .Audio("audio/wav", "clip.wav", bytes).ToJson();
        Assert.Equal("input_audio", audio["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(bytes),
            audio["input_audio"]!["data"]!.GetValue<string>());
    }
}
