using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Gatherum.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Tests;

[Collection("postgres")]
public class NodeServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private string connectionString = "";
    private GatherumDbContext db = null!;
    private NodeService nodes = null!;
    private readonly ManualClock clock = new();
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        connectionString = await postgres.CreateDatabaseAsync();
        db = PostgresFixture.CreateContext(connectionString);
        nodes = new NodeService(db, new DefaultNodeAuthorizer(), clock);
        jess = await AddUserAsync("jess");
        sam = await AddUserAsync("sam");
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    [Fact]
    public async Task Children_are_ordered_and_positions_are_dense()
    {
        var parent = await nodes.CreatePageAsync(jess, null, "parent");
        var a = await nodes.CreatePageAsync(jess, parent.Id, "a");
        var b = await nodes.CreatePageAsync(jess, parent.Id, "b");
        var c = await nodes.CreatePageAsync(jess, parent.Id, "c");

        await nodes.DeleteAsync(jess, b.Id);

        var children = await nodes.GetChildrenAsync(jess, parent.Id);
        Assert.Equal(["a", "c"], children.Select(n => n.Title));
        Assert.Equal([0, 1], children.Select(n => n.Position));
        Assert.Equal(a.Id, children[0].Id);
        Assert.Equal(c.Id, children[1].Id);
    }

    [Fact]
    public async Task Move_reparents_and_renumbers_both_sides()
    {
        var source = await nodes.CreatePageAsync(jess, null, "source");
        var target = await nodes.CreatePageAsync(jess, null, "target");
        var one = await nodes.CreatePageAsync(jess, source.Id, "one");
        var two = await nodes.CreatePageAsync(jess, source.Id, "two");
        var existing = await nodes.CreatePageAsync(jess, target.Id, "existing");

        await nodes.MoveAsync(jess, two.Id, target.Id, position: 0);

        var oldSiblings = await nodes.GetChildrenAsync(jess, source.Id);
        Assert.Equal([one.Id], oldSiblings.Select(n => n.Id));
        Assert.Equal([0], oldSiblings.Select(n => n.Position));

        var newSiblings = await nodes.GetChildrenAsync(jess, target.Id);
        Assert.Equal([two.Id, existing.Id], newSiblings.Select(n => n.Id));
        Assert.Equal([0, 1], newSiblings.Select(n => n.Position));
    }

    [Fact]
    public async Task Reordering_within_the_same_parent_works()
    {
        var parent = await nodes.CreatePageAsync(jess, null, "parent");
        var a = await nodes.CreatePageAsync(jess, parent.Id, "a");
        var b = await nodes.CreatePageAsync(jess, parent.Id, "b");
        var c = await nodes.CreatePageAsync(jess, parent.Id, "c");

        await nodes.MoveAsync(jess, c.Id, parent.Id, position: 0);

        var children = await nodes.GetChildrenAsync(jess, parent.Id);
        Assert.Equal([c.Id, a.Id, b.Id], children.Select(n => n.Id));
    }

    [Fact]
    public async Task A_node_cannot_move_into_its_own_subtree()
    {
        var root = await nodes.CreatePageAsync(jess, null, "root");
        var child = await nodes.CreatePageAsync(jess, root.Id, "child");
        var grandchild = await nodes.CreatePageAsync(jess, child.Id, "grandchild");

        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.MoveAsync(jess, root.Id, grandchild.Id));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.MoveAsync(jess, root.Id, root.Id));
    }

    [Fact]
    public async Task Private_subtrees_hide_from_everyone_but_the_owner()
    {
        var secret = await nodes.CreatePageAsync(jess, null, "secret");
        var inside = await nodes.CreatePageAsync(jess, secret.Id, "inside");
        await nodes.SetPrivateAsync(jess, secret.Id, true);

        Assert.Equal(2, (await nodes.GetTreeAsync(jess)).Count);
        Assert.Empty(await nodes.GetTreeAsync(sam));
        await Assert.ThrowsAsync<Gatherum.Core.NotFoundException>(
            () => nodes.GetWithBodyAsync(sam, inside.Id));

        await nodes.SetPrivateAsync(jess, secret.Id, false);
        Assert.Equal(2, (await nodes.GetTreeAsync(sam)).Count);
    }

    [Fact]
    public async Task Moving_into_a_private_subtree_inherits_privacy()
    {
        var secret = await nodes.CreatePageAsync(jess, null, "secret");
        await nodes.SetPrivateAsync(jess, secret.Id, true);
        var wanderer = await nodes.CreatePageAsync(sam, null, "wanderer");

        // Sam can't see jess's private node, so jess performs the move.
        await nodes.MoveAsync(jess, wanderer.Id, secret.Id);
        Assert.DoesNotContain(await nodes.GetTreeAsync(sam), n => n.Id == wanderer.Id);

        await nodes.MoveAsync(jess, wanderer.Id, null);
        Assert.Contains(await nodes.GetTreeAsync(sam), n => n.Id == wanderer.Id);
    }

    [Fact]
    public async Task Only_the_owner_can_toggle_privacy()
    {
        var page = await nodes.CreatePageAsync(jess, null, "mine");

        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.SetPrivateAsync(sam, page.Id, true));
    }

    [Fact]
    public async Task Saving_a_page_with_mentions_creates_backlinks()
    {
        var target = await nodes.CreatePageAsync(jess, null, "target");
        var source = await nodes.CreatePageAsync(jess, null, "source");
        var doc = PageMarkdown.ToDocJson($"See [@target](node://{target.Id}).");

        await nodes.SavePageAsync(jess, source.Id, doc);

        var backlinks = await nodes.GetBacklinksAsync(jess, target.Id);
        Assert.Equal([source.Id], backlinks.Select(n => n.Id));

        await nodes.SavePageAsync(jess, source.Id, PageMarkdown.EmptyDoc);
        Assert.Empty(await nodes.GetBacklinksAsync(jess, target.Id));
    }

    [Fact]
    public async Task Tags_contribute_to_search_text_and_can_be_removed()
    {
        var page = await nodes.CreatePageAsync(jess, null, "quadlets");
        await nodes.AddTagAsync(jess, page.Id, " Podman ");
        await nodes.AddTagAsync(jess, page.Id, "podman");

        var fresh = await ReloadAsync(page.Id);
        Assert.Contains("podman", fresh.SearchText);
        Assert.Single(fresh.Tags);

        await nodes.RemoveTagAsync(jess, page.Id, "PODMAN");
        fresh = await ReloadAsync(page.Id);
        Assert.DoesNotContain("podman", fresh.SearchText);
        Assert.DoesNotContain(await nodes.ListTagsAsync(jess), t => t.Name == "podman");
    }

    [Fact]
    public async Task Distinct_saves_create_revisions_and_rapid_saves_collapse()
    {
        var page = await nodes.CreatePageAsync(jess, null, "draft");
        await nodes.SavePageAsync(jess, page.Id, PageMarkdown.ToDocJson("first"));
        await nodes.SavePageAsync(jess, page.Id, PageMarkdown.ToDocJson("first, revised"));

        Assert.Single(await nodes.GetRevisionsAsync(jess, page.Id));

        clock.Advance(TimeSpan.FromMinutes(10));
        await nodes.SavePageAsync(jess, page.Id, PageMarkdown.ToDocJson("second"));

        var revisions = await nodes.GetRevisionsAsync(jess, page.Id);
        Assert.Equal(2, revisions.Count);
        Assert.Equal("second", PageMarkdown.ToPlainText(revisions[0].Doc));
        Assert.Equal("first, revised", PageMarkdown.ToPlainText(revisions[1].Doc));
    }

    [Fact]
    public async Task Restoring_a_revision_brings_back_its_content_as_a_new_revision()
    {
        var page = await nodes.CreatePageAsync(jess, null, "draft");
        await nodes.SavePageAsync(jess, page.Id, PageMarkdown.ToDocJson("original"));
        clock.Advance(TimeSpan.FromMinutes(10));
        await nodes.SavePageAsync(jess, page.Id, PageMarkdown.ToDocJson("rewritten"));
        clock.Advance(TimeSpan.FromMinutes(10));

        await nodes.RestoreRevisionAsync(jess, page.Id, 1);

        var fresh = await ReloadAsync(page.Id);
        Assert.Equal("original", PageMarkdown.ToPlainText(fresh.Page!.Doc));
        Assert.Equal(3, (await nodes.GetRevisionsAsync(jess, page.Id)).Count);
    }

    private async Task<Node> ReloadAsync(Guid id)
    {
        db.ChangeTracker.Clear();
        return await nodes.GetWithBodyAsync(jess, id);
    }

    private async Task<Guid> AddUserAsync(string name)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Subject = name,
            Email = $"{name}@example.org",
            DisplayName = name,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
