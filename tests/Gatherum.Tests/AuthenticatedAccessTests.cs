using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gatherum.Tests;

[Collection("postgres")]
public class AuthenticatedAccessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private NodeService nodes = null!;
    private AccessService access = null!;
    private FileService files = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        nodes = harness.Nodes;
        access = harness.Access;
        files = harness.Files;
        jess = await harness.AddUserAsync("jess");
        sam = await harness.AddUserAsync("sam");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    private Task<Node> NewPageAsync(Guid userId, Guid? parentId, string title) =>
        files.CreateTextNodeAsync(userId, parentId, title, "");

    private async Task<bool> InTreeOfAsync(Guid? viewerId, Guid nodeId) =>
        (await nodes.GetTreeAsync(viewerId)).Any(n => n.Id == nodeId);

    [Fact]
    public async Task Everyone_signed_in_sees_it_and_nobody_signed_out_does()
    {
        var page = await NewPageAsync(jess, null, "roster");
        await access.SetAccessAsync(jess, page.Id, AccessMode.Authenticated);

        Assert.True(await InTreeOfAsync(sam, page.Id));
        Assert.False(await InTreeOfAsync(null, page.Id));
    }

    [Fact]
    public async Task It_admits_reading_as_well_as_listing()
    {
        var page = await NewPageAsync(jess, null, "roster");
        await access.SetAccessAsync(jess, page.Id, AccessMode.Authenticated);

        Assert.NotNull(await nodes.GetVisibleAsync(sam, page.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => nodes.GetVisibleAsync(null, page.Id));
    }

    [Fact]
    public async Task Seeing_it_is_still_not_editing_it()
    {
        var page = await NewPageAsync(jess, null, "roster");
        await access.SetAccessAsync(jess, page.Id, AccessMode.Authenticated);

        await Assert.ThrowsAnyAsync<Exception>(() => files.SaveTextAsync(sam, page.Id, "mine now"));
    }

    [Fact]
    public async Task It_carries_down_a_subtree_like_every_other_declaration()
    {
        var folder = await NewPageAsync(jess, null, "season");
        var page = await NewPageAsync(jess, folder.Id, "sprites");
        await access.SetAccessAsync(jess, folder.Id, AccessMode.Authenticated);

        Assert.True(await InTreeOfAsync(sam, page.Id));
        Assert.False(await InTreeOfAsync(null, page.Id));
    }

    /// <summary>The case that forced two axes rather than one ordered scale. Under a single
    /// enum this page's reach would be the maximum of "authenticated" and "with link", and
    /// whichever lost would take its half of the answer with it.</summary>
    [Fact]
    public async Task An_unlisted_page_inside_an_authenticated_folder_is_both()
    {
        var folder = await NewPageAsync(jess, null, "season");
        var page = await NewPageAsync(jess, folder.Id, "sprites");
        await access.SetAccessAsync(jess, folder.Id, AccessMode.Authenticated);
        await access.SetAccessAsync(jess, page.Id, AccessMode.Unlisted);

        var stored = await nodes.GetVisibleAsync(jess, page.Id);
        Assert.Equal(NodeReach.WithLink, stored.Reach);
        Assert.True(stored.ListedToSignedIn);

        // Listed to the people the folder was shared with...
        Assert.True(await InTreeOfAsync(sam, page.Id));
        // ...and still reachable by a stranger holding the link, but never listed to one.
        Assert.NotNull(await nodes.GetVisibleAsync(null, page.Id));
        Assert.False(await InTreeOfAsync(null, page.Id));
    }

    [Fact]
    public async Task Inherit_false_keeps_a_subtree_tighter_than_what_contains_it()
    {
        var folder = await NewPageAsync(jess, null, "season");
        var page = await NewPageAsync(jess, folder.Id, "secret");
        await access.SetAccessAsync(jess, folder.Id, AccessMode.Authenticated);
        await access.SetAccessAsync(jess, page.Id, AccessMode.Private, inherit: false);

        Assert.False(await InTreeOfAsync(sam, page.Id));
        Assert.True(await InTreeOfAsync(jess, page.Id));
    }

    [Fact]
    public async Task Going_back_to_private_takes_it_away_again()
    {
        var page = await NewPageAsync(jess, null, "roster");
        await access.SetAccessAsync(jess, page.Id, AccessMode.Authenticated);
        await access.SetAccessAsync(jess, page.Id, AccessMode.Private);

        Assert.False(await InTreeOfAsync(sam, page.Id));
    }

    [Fact]
    public async Task Only_the_owner_may_declare_it()
    {
        var page = await NewPageAsync(jess, null, "roster");

        await Assert.ThrowsAnyAsync<Exception>(
            () => access.SetAccessAsync(sam, page.Id, AccessMode.Authenticated));
    }

    /// <summary>A cold rebuild has to bring this back, or the disk would disagree with the
    /// database about who may read something — and it would come back wrong in whichever
    /// direction the sidecar failed to say.</summary>
    [Fact]
    public async Task It_survives_a_rebuild_from_the_directories()
    {
        var page = await NewPageAsync(jess, null, "roster");
        await access.SetAccessAsync(jess, page.Id, AccessMode.Authenticated);

        await using var rebuilt = harness.Fork(await postgres.CreateDatabaseAsync());
        await rebuilt.AddUserAsync("jess");
        var samAgain = await rebuilt.AddUserAsync("sam");
        await new Reindexer(rebuilt.Db, rebuilt.Storage, rebuilt.Metadata, rebuilt.Roots,
            rebuilt.Nodes, rebuilt.Access, rebuilt.Categories,
            [new Gatherum.Infrastructure.Extraction.PlainTextExtractor()],
            rebuilt.Clock, NullLogger<Reindexer>.Instance).RunAsync();

        var back = rebuilt.Db.Nodes.Single(n => n.Title == "roster");
        Assert.Equal(AccessMode.Authenticated, back.Access);
        Assert.True(back.ListedToSignedIn);
        Assert.Contains(await rebuilt.Nodes.GetTreeAsync(samAgain), n => n.Id == back.Id);
    }
}
