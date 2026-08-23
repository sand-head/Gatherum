using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gatherum.Tests;

/// <summary>The reason the filesystem is the system of record: everything here is
/// recovered from a directory, with the database thrown away in between.</summary>
[Collection("postgres")]
public class ReindexTests(PostgresFixture postgres) : IAsyncLifetime
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

    public Task DisposeAsync() => harness.DisposeAsync().AsTask();

    private Reindexer NewReindexer(ServiceHarness host) => new(
        host.Db, host.Storage, host.Metadata, host.Roots, host.Nodes, host.Access,
        host.Categories, [new Gatherum.Infrastructure.Extraction.PlainTextExtractor()],
        host.Clock, NullLogger<Reindexer>.Instance);

    [Fact]
    public async Task Losing_the_database_costs_nothing_but_recomputation()
    {
        var homelab = await harness.Files.CreateTextNodeAsync(jess, null, "Homelab", "the rack");
        var podman = await harness.Files.CreateTextNodeAsync(jess, homelab.Id, "Podman",
            "quadlets, mostly");
        await harness.Categories.AddAsync(jess, podman.Id, "Homelab/Containers");
        await harness.Access.GrantAsync(jess, homelab.Id, sam, AccessRole.Editor);

        var published = await harness.Files.CreateTextNodeAsync(jess, null, "Published", "hello");
        await harness.Access.SetAccessAsync(jess, published.Id, AccessMode.Public);

        // Everything above is now on disk. Throw the index away entirely.
        await using var rebuilt = harness.Fork(await postgres.CreateDatabaseAsync());
        Assert.Empty(rebuilt.Db.Nodes.ToList());

        // Users are the one thing genuinely lost with the database, and they come back by
        // signing in. Their ids are new — which is exactly why a grant is recorded on
        // disk by root directory and not by a Guid the old database made up.
        var jessAgain = await rebuilt.AddUserAsync("jess");
        var samAgain = await rebuilt.AddUserAsync("sam");
        Assert.NotEqual(jess, jessAgain);

        var report = await NewReindexer(rebuilt).RunAsync();

        Assert.Equal(0, report.Removed);
        Assert.Empty(report.SkippedRoots);

        // The tree came back, with its shape.
        var tree = await rebuilt.Nodes.GetTreeAsync(jessAgain);
        Assert.Contains(tree, n => n.Title == "Homelab");
        Assert.Contains(tree, n => n.Title == "Podman");

        var recoveredPodman = tree.Single(n => n.Title == "Podman");
        var recoveredHomelab = tree.Single(n => n.Title == "Homelab");
        Assert.Equal(recoveredHomelab.Id, recoveredPodman.ParentId);

        // Ids survived, because they were written down beside the bytes.
        Assert.Equal(podman.Id, recoveredPodman.Id);

        // So did the content.
        Assert.Equal("quadlets, mostly", await rebuilt.Files.GetTextAsync(jessAgain, podman.Id));

        // And the categories.
        var body = await rebuilt.Nodes.GetWithBodyAsync(jessAgain, podman.Id);
        Assert.Equal(["homelab/containers"], body.Categories.Select(c => c.Category!.Path));

        // And — the one that would be unsafe to get wrong — the sharing.
        Assert.Contains(await rebuilt.Nodes.GetTreeAsync(samAgain), n => n.Id == podman.Id);
        Assert.Equal([published.Id],
            (await rebuilt.Nodes.GetTreeAsync(null)).Select(n => n.Id));
    }

    [Fact]
    public async Task A_directory_nobody_prepared_still_reads_as_a_wiki()
    {
        // No Gatherum ever touched these: no ids, no sidecar, no database rows.
        var root = harness.StorageRoot;
        Directory.CreateDirectory(Path.Combine(root, "jess", "Notes"));
        await File.WriteAllTextAsync(Path.Combine(root, "jess", "Notes", "Thermals.md"),
            "the closet gets hot");
        await File.WriteAllTextAsync(Path.Combine(root, "jess", "todo.txt"), "buy fans");

        var report = await NewReindexer(harness).RunAsync();

        Assert.Equal(3, report.Added);   // two files and the directory holding one of them

        var tree = await harness.Nodes.GetTreeAsync(jess);
        // The filename is the title, with no metadata consulted.
        Assert.Equal(["Notes", "Thermals", "todo"],
            tree.Select(n => n.Title).OrderBy(t => t));

        var thermals = tree.Single(n => n.Title == "Thermals");
        Assert.Equal("the closet gets hot", await harness.Files.GetTextAsync(jess, thermals.Id));

        // Found by its content, which is the whole promise.
        Assert.Contains(await harness.Search.SearchAsync(jess, "closet"),
            hit => hit.Id == thermals.Id);

        // And private, because nothing said otherwise.
        Assert.Empty(await harness.Nodes.GetTreeAsync(sam));
        Assert.Empty(await harness.Nodes.GetTreeAsync(null));
    }

    [Fact]
    public async Task A_file_edited_behind_gatherums_back_becomes_a_new_version()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Notes", "first");
        var onDisk = Path.Combine(harness.StorageRoot, "jess", "Notes.md");

        await File.WriteAllTextAsync(onDisk, "somebody else wrote this");
        await NewReindexer(harness).RunAsync();

        // The disk wins, and the version that was there is kept rather than corrected.
        var body = await harness.Nodes.GetWithBodyAsync(jess, page.Id);
        Assert.Equal(2, body.File!.Versions.Count);
        Assert.Equal("somebody else wrote this", await harness.Files.GetTextAsync(jess, page.Id));
    }

    [Fact]
    public async Task A_root_no_user_owns_is_left_alone_rather_than_adopted()
    {
        Directory.CreateDirectory(Path.Combine(harness.StorageRoot, "nobody"));
        await File.WriteAllTextAsync(
            Path.Combine(harness.StorageRoot, "nobody", "orphan.md"), "whose is this?");

        var report = await NewReindexer(harness).RunAsync();

        Assert.Equal(["nobody"], report.SkippedRoots);
        Assert.Empty(await harness.Nodes.GetTreeAsync(jess));
    }
}
