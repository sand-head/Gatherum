using System.Text;
using Gatherum.Core;
using Gatherum.Core.Abstractions;
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
    public async Task Content_is_a_plain_file_at_a_plain_path()
    {
        var path = new NodePath("alice", "Homelab/Podman.md");

        var blob = await storage.WriteAsync(path, Stream("quadlets, mostly"));

        // The whole point: somebody with no Gatherum finds a readable file where they
        // would expect one, named what it is called.
        var onDisk = Path.Combine(root, "alice", "Homelab", "Podman.md");
        Assert.True(File.Exists(onDisk));
        Assert.Equal("quadlets, mostly", await File.ReadAllTextAsync(onDisk));
        Assert.Equal(64, blob.Hash.Length);
        Assert.True(await storage.ExistsAsync(path));

        await using var stream = await storage.OpenReadAsync(path);
        using var reader = new StreamReader(stream);
        Assert.Equal("quadlets, mostly", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task A_walk_finds_the_files_and_skips_gatherums_own_bookkeeping()
    {
        await storage.WriteAsync(new NodePath("alice", "Homelab/Podman.md"), Stream("a"));
        await storage.WriteAsync(new NodePath("alice", "photo.jpg"), Stream("b"));
        await storage.ArchiveAsync("alice", Stream("an older draft"));

        Assert.Equal(["Homelab/Podman.md", "photo.jpg"],
            storage.Walk("alice").Select(p => p.Relative).OrderBy(p => p));
        Assert.Equal(["alice"], storage.Roots());
    }

    [Fact]
    public async Task History_is_content_addressed_beside_the_files_and_deduplicates()
    {
        var bytes = "the same bytes twice"u8.ToArray();

        var first = await storage.ArchiveAsync("alice", new MemoryStream(bytes));
        var second = await storage.ArchiveAsync("alice", new MemoryStream(bytes));

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(bytes.Length, first.SizeBytes);
        Assert.True(await storage.ArchivedAsync("alice", first.Hash));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "alice", ".gatherum", "versions"),
            "*", SearchOption.AllDirectories));

        await using var stream = await storage.OpenArchiveAsync("alice", first.Hash);
        using var reader = new StreamReader(stream);
        Assert.Equal("the same bytes twice", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Nothing_reaches_outside_the_root_it_was_asked_for()
    {
        // Ownership is the directory, so a path that leaves one is a way to claim
        // somebody else's file. None of these are served.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.OpenReadAsync(new NodePath("alice", "../bob/secret.md")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.OpenReadAsync(new NodePath("../..", "passwd")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.OpenReadAsync(new NodePath("alice", ".gatherum/versions/x")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.OpenReadAsync(new NodePath("alice", "")));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            storage.OpenReadAsync(new NodePath("alice", "nothing-here.md")));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            storage.OpenArchiveAsync("alice", new string('0', 64)));
    }

    [Fact]
    public async Task A_symlink_out_of_a_root_is_not_a_sharing_mechanism()
    {
        await storage.WriteAsync(new NodePath("bob", "secret.md"), Stream("bob's"));
        Directory.CreateDirectory(Path.Combine(root, "alice"));
        File.CreateSymbolicLink(
            Path.Combine(root, "alice", "borrowed.md"),
            Path.Combine(root, "bob", "secret.md"));

        // The link is real on disk, and deliberately not content as far as Gatherum
        // is concerned: indexing it would launder bob's file into alice's ownership.
        Assert.DoesNotContain(storage.Walk("alice"), p => p.Relative == "borrowed.md");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.OpenReadAsync(new NodePath("alice", "borrowed.md")));
    }

    private static MemoryStream Stream(string content) =>
        new(Encoding.UTF8.GetBytes(content));

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
