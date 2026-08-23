using Gatherum.Core;
using Gatherum.Core.Domain;
using Gatherum.Infrastructure.Extraction;
using SlopEdit.Docx;

namespace Gatherum.Tests;

public class DocxExtractionTests
{
    [Fact]
    public void Claims_docx_by_media_type_and_by_extension()
    {
        var extractor = new DocxTextExtractor();

        Assert.True(extractor.CanExtract(MediaTypes.Docx, "report.docx"));
        Assert.True(extractor.CanExtract(MediaTypes.Binary, "report.docx"));
        Assert.False(extractor.CanExtract(MediaTypes.PlainText, "notes.txt"));
    }

    [Fact]
    public async Task Extracts_a_docx_body_as_its_markdown_rendering()
    {
        using var docx = new MemoryStream();
        DocxConverter.FromMarkdown("# Quarterly report\n\nNumbers went **up**.", docx);
        docx.Position = 0;

        var text = await new DocxTextExtractor().ExtractAsync(docx, MediaTypes.Docx, "q.docx");

        Assert.Contains("# Quarterly report", text);
        Assert.Contains("Numbers went **up**.", text);
    }
}

[Collection("postgres")]
public class DocxEditingTests(PostgresFixture postgres) : IAsyncLifetime
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

    private static byte[] Docx(string markdown)
    {
        using var output = new MemoryStream();
        DocxConverter.FromMarkdown(markdown, output);
        return output.ToArray();
    }

    private async Task<Guid> UploadDocxAsync(string markdown)
    {
        var node = await harness.Files.CreateFileNodeAsync(jess, null, "report.docx",
            MediaTypes.Docx, new MemoryStream(Docx(markdown)));
        return node.Id;
    }

    [Fact]
    public async Task Rapid_document_saves_collapse_like_text_autosave()
    {
        var id = await UploadDocxAsync("draft");

        await harness.Files.SaveBinaryAsync(jess, id, Docx("draft, revised"));
        await harness.Files.SaveBinaryAsync(jess, id, Docx("draft, revised again"));

        var fresh = await harness.ReloadAsync(jess, id);
        Assert.Single(fresh.File!.Versions);
        Assert.Contains("revised again", fresh.File.Current.ExtractedText);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        await harness.Files.SaveBinaryAsync(jess, id, Docx("later"));

        fresh = await harness.ReloadAsync(jess, id);
        Assert.Equal(2, fresh.File!.Versions.Count);
    }

    [Fact]
    public async Task Another_authors_document_save_gets_its_own_version()
    {
        var id = await UploadDocxAsync("by jess");
        await harness.Access.GrantAsync(jess, id, sam, AccessRole.Editor);

        await harness.Files.SaveBinaryAsync(sam, id, Docx("by sam"));

        var fresh = await harness.ReloadAsync(jess, id);
        Assert.Equal(2, fresh.File!.Versions.Count);
        Assert.Equal(sam, fresh.File.Current.UploadedById);
    }

    [Fact]
    public async Task Document_saves_refuse_nodes_that_are_not_documents()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "a page");

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            harness.Files.SaveBinaryAsync(jess, page.Id, Docx("smuggled")));
    }

    [Fact]
    public async Task Mentions_inside_a_document_become_backlinks()
    {
        var target = await harness.Files.CreateTextNodeAsync(jess, null, "Quadlet notes");
        var id = await UploadDocxAsync("plain start");

        await harness.Files.SaveBinaryAsync(jess, id,
            Docx($"See [@Quadlet notes](node://{target.Id}) for details."));

        var backlinks = await harness.Nodes.GetBacklinksAsync(jess, target.Id);
        Assert.Contains(backlinks, n => n.Id == id);
    }

    [Fact]
    public async Task Uploaded_documents_are_searchable_by_their_prose()
    {
        await UploadDocxAsync("# Quarterly report\n\nRevenue objectives were exceeded.");

        var hits = await harness.Search.SearchAsync(jess, "revenue objectives");

        Assert.Contains(hits, h => h.Title == "report.docx");
    }
}
