using System.Text;
using Gatherum.Core.Domain;

namespace Gatherum.Tests;

[Collection("postgres")]
public class FileVersionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        jess = await harness.AddUserAsync("jess");
        sam = await harness.AddUserAsync("sam");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task Rapid_saves_by_one_author_collapse_into_one_version()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "draft");
        await harness.Files.SaveTextAsync(jess, page.Id, "first");
        await harness.Files.SaveTextAsync(jess, page.Id, "first, revised");

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Single(fresh.File!.Versions);
        Assert.Equal("first, revised", fresh.File.Current.ExtractedText);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await harness.Files.SaveTextAsync(jess, page.Id, "second");

        fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(2, fresh.File!.Versions.Count);
        Assert.Equal("second", fresh.File.Current.ExtractedText);
    }

    [Fact]
    public async Task A_different_author_always_gets_a_fresh_version()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "draft", "by jess");

        await harness.Files.SaveTextAsync(sam, page.Id, "by sam");

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(2, fresh.File!.Versions.Count);
        Assert.Equal(sam, fresh.File.Current.UploadedById);
    }

    [Fact]
    public async Task Restore_brings_back_old_content_as_the_newest_version()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "draft", "original");
        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await harness.Files.SaveTextAsync(jess, page.Id, "rewritten");
        harness.Clock.Advance(TimeSpan.FromMinutes(10));

        await harness.Files.RestoreVersionAsync(jess, page.Id, 1);

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(3, fresh.File!.Versions.Count);
        Assert.Equal("original", fresh.File.Current.ExtractedText);
        Assert.Equal("original", await harness.Files.GetTextAsync(jess, page.Id));
    }

    [Fact]
    public async Task Reupload_appends_a_version_and_old_bytes_stay_retrievable()
    {
        var v1 = new MemoryStream(Encoding.UTF8.GetBytes("version one"));
        var node = await harness.Files.CreateFileNodeAsync(jess, null, "notes.txt", "text/plain", v1);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        var v2 = new MemoryStream(Encoding.UTF8.GetBytes("version two"));
        await harness.Files.UploadVersionAsync(jess, node.Id, "notes.txt", "text/plain", v2);

        var fresh = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(2, fresh.File!.Versions.Count);

        var old = await harness.Files.OpenContentAsync(jess, node.Id, versionNumber: 1);
        using var reader = new StreamReader(old.Stream);
        Assert.Equal("version one", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Uploaded_markdown_is_a_page_and_editable_text_is_recognized()
    {
        var content = new MemoryStream(Encoding.UTF8.GetBytes("# uploaded"));
        var node = await harness.Files.CreateFileNodeAsync(jess, null, "notes.md",
            "application/octet-stream", content);

        var fresh = await harness.ReloadAsync(jess, node.Id);
        Assert.Equal(NodeKind.Page, fresh.Kind);
        Assert.Equal(MediaTypes.Markdown, fresh.MediaType);
        Assert.True(MediaTypes.IsText(fresh.MediaType, "notes.md"));
    }

    [Fact]
    public async Task Binary_nodes_refuse_text_editing()
    {
        var bytes = new MemoryStream([0xFF, 0xD8, 0xFF, 0x00]);
        var node = await harness.Files.CreateFileNodeAsync(jess, null, "photo.jpg", "image/jpeg", bytes);

        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => harness.Files.SaveTextAsync(jess, node.Id, "not text"));
    }
}
