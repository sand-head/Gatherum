using System.Text;
using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gatherum.Tests;

/// <summary>Bookmarks against the real stack, with <see cref="FakePageArchiver"/>
/// standing in for the web: what a capture becomes, where it lands, how it is found,
/// and what surviving the database means for it.</summary>
[Collection("postgres")]
public class BookmarkServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private Guid jess;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        jess = await harness.AddUserAsync("jess");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    private static ArchivedPage Snapshot(string title, string html) =>
        new(title, title + ".html", "text/html", Encoding.UTF8.GetBytes(html));

    [Fact]
    public async Task Saving_a_url_captures_the_page_as_a_file_node()
    {
        harness.Archiver.Pages["https://example.org/thermals"] = Snapshot("Closet thermals",
            "<html><body><p>the closet runs hot in summer</p></body></html>");

        var node = await harness.Bookmarks.SaveAsync(jess, null, "https://example.org/thermals");

        Assert.Equal("Closet thermals", node.Title);
        Assert.Equal(MediaTypes.Html, node.MediaType);
        Assert.Equal(NodeKind.File, node.Kind);
        Assert.Equal("https://example.org/thermals", node.File!.SourceUrl);

        // The capture is a plain file in the owner's directory, like everything else.
        Assert.True(File.Exists(
            Path.Combine(harness.StorageRoot, "jess", "Closet thermals.html")));

        // Findable by what the page says — words, not markup.
        var hit = Assert.Single(await harness.Search.SearchAsync(jess, "closet summer"));
        Assert.Equal(node.Id, hit.Id);
        Assert.DoesNotContain("<body>", node.File.Current.ExtractedText);

        // And by where it came from.
        Assert.Contains(await harness.Search.SearchAsync(jess, "example.org"),
            r => r.Id == node.Id);
    }

    [Fact]
    public async Task Capturing_again_is_a_new_version_with_the_old_one_kept()
    {
        var url = "https://example.org/news";
        harness.Archiver.Pages[url] = Snapshot("News", "<html><body>first edition</body></html>");
        var node = await harness.Bookmarks.SaveAsync(jess, null, url);

        harness.Clock.Advance(TimeSpan.FromHours(1));
        harness.Archiver.Pages[url] = Snapshot("News", "<html><body>second edition</body></html>");
        await harness.Bookmarks.CaptureAgainAsync(jess, node.Id);

        var reloaded = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(2, reloaded.File!.Current.Number);
        Assert.Equal(url, reloaded.File.SourceUrl);
        Assert.Contains("second edition", await harness.Files.GetTextAsync(jess, node.Id));

        // The superseded capture reads back the way an archive's older crawl does.
        var first = await harness.Files.OpenContentAsync(jess, node.Id, 1);
        await using (first.Stream)
        using (var reader = new StreamReader(first.Stream))
        {
            Assert.Contains("first edition", await reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task A_url_serving_a_document_is_kept_as_that_document()
    {
        harness.Archiver.Pages["https://example.org/manual.pdf"] = new ArchivedPage(
            "manual", "manual.pdf", "application/pdf", [0x25, 0x50, 0x44, 0x46]);

        var node = await harness.Bookmarks.SaveAsync(jess, null, "https://example.org/manual.pdf");

        Assert.Equal("application/pdf", node.MediaType);
        Assert.Equal("manual", node.Title);
        Assert.Equal("https://example.org/manual.pdf", node.File!.SourceUrl);
    }

    [Fact]
    public async Task A_bad_url_or_a_refusing_server_is_an_error_a_person_can_read()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Bookmarks.SaveAsync(jess, null, "ftp://example.org/files"));
        await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Bookmarks.SaveAsync(jess, null, "not a url"));
        Assert.Equal(0, harness.Archiver.Fetches);

        harness.Archiver.Refusal = "https://down.example/ answered 503 Service Unavailable.";
        var refused = await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Bookmarks.SaveAsync(jess, null, "https://down.example/"));
        Assert.Contains("503", refused.Message);

        // A failed capture leaves nothing behind.
        Assert.Empty(await harness.Nodes.GetTreeAsync(jess));
    }

    [Fact]
    public async Task Capture_again_refuses_a_node_that_was_never_a_bookmark()
    {
        var upload = new MemoryStream(Encoding.UTF8.GetBytes("plain notes"));
        var node = await harness.Files.CreateFileNodeAsync(jess, null, "notes.txt",
            "text/plain", upload);

        await Assert.ThrowsAsync<ValidationException>(() =>
            harness.Bookmarks.CaptureAgainAsync(jess, node.Id));
    }

    [Fact]
    public async Task The_source_url_survives_losing_the_database()
    {
        harness.Archiver.Pages["https://example.org/thermals"] = Snapshot("Closet thermals",
            "<html><body>the closet runs hot</body></html>");
        var node = await harness.Bookmarks.SaveAsync(jess, null, "https://example.org/thermals");

        await using var rebuilt = harness.Fork(await postgres.CreateDatabaseAsync());
        var jessAgain = await rebuilt.AddUserAsync("jess");
        await new Reindexer(rebuilt.Db, rebuilt.Storage, rebuilt.Metadata, rebuilt.Roots,
            rebuilt.Nodes, rebuilt.Access, rebuilt.Categories,
            [new Gatherum.Infrastructure.Extraction.HtmlTextExtractor(),
                new Gatherum.Infrastructure.Extraction.PlainTextExtractor()],
            rebuilt.Clock, NullLogger<Reindexer>.Instance).RunAsync();

        var recovered = await rebuilt.Nodes.GetWithBodyAsync(jessAgain, node.Id);
        Assert.Equal("Closet thermals", recovered.Title);
        Assert.Equal("https://example.org/thermals", recovered.File!.SourceUrl);

        // Which means capturing again still works in the rebuilt world.
        rebuilt.Archiver.Pages["https://example.org/thermals"] = Snapshot("Closet thermals",
            "<html><body>now with fans</body></html>");
        await rebuilt.Bookmarks.CaptureAgainAsync(jessAgain, node.Id);
        Assert.Contains("fans", await rebuilt.Files.GetTextAsync(jessAgain, node.Id));
    }
}
