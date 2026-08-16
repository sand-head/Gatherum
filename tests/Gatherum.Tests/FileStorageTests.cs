using System.Text;
using Gatherum.Core;
using Gatherum.Infrastructure.Extraction;
using Gatherum.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Gatherum.Tests;

public class FileStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"gatherum-test-{Guid.NewGuid():N}");
    private readonly FileSystemStorage storage;

    public FileStorageTests() => storage = new FileSystemStorage(Options.Create(
        new GatherumOptions { Storage = new StorageOptions { Root = root } }));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Content_is_addressed_by_its_hash_and_deduplicated()
    {
        var bytes = Encoding.UTF8.GetBytes("the same bytes twice");

        var first = await storage.SaveAsync(new MemoryStream(bytes));
        var second = await storage.SaveAsync(new MemoryStream(bytes));

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(bytes.Length, first.SizeBytes);
        Assert.Equal(64, first.Hash.Length);
        Assert.True(await storage.ExistsAsync(first.Hash));

        await using var stream = await storage.OpenReadAsync(first.Hash);
        using var reader = new StreamReader(stream);
        Assert.Equal("the same bytes twice", await reader.ReadToEndAsync());

        Assert.Single(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Unknown_hashes_do_not_resolve()
    {
        var missing = new string('0', 64);
        Assert.False(await storage.ExistsAsync(missing));
        await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(missing));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.OpenReadAsync("../etc/passwd"));
    }

    [Fact]
    public async Task Plain_text_extractor_claims_code_and_returns_it_verbatim()
    {
        var extractor = new PlainTextExtractor();
        Assert.True(extractor.CanExtract("text/x-python", "script.py"));
        Assert.True(extractor.CanExtract("application/octet-stream", "Program.cs"));
        Assert.False(extractor.CanExtract("application/octet-stream", "binary.bin"));

        var text = await extractor.ExtractAsync(
            new MemoryStream("print('hi')"u8.ToArray()), "text/x-python", "script.py");
        Assert.Equal("print('hi')", text);
    }

    [Fact]
    public void Pdf_and_image_extractors_claim_their_formats()
    {
        Assert.True(new PdfTextExtractor().CanExtract("application/pdf", "doc.pdf"));
        Assert.True(new ImageMetadataExtractor().CanExtract("image/jpeg", "photo.jpg"));
        Assert.False(new ImageMetadataExtractor().CanExtract("image/svg+xml", "logo.svg"));
    }
}
