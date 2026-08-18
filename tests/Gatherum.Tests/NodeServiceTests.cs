using Gatherum.Core.Domain;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class NodeServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private NodeService nodes = null!;
    private FileService files = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        nodes = harness.Nodes;
        files = harness.Files;
        jess = await harness.AddUserAsync("jess");
        sam = await harness.AddUserAsync("sam");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    private Task<Node> NewPageAsync(Guid userId, Guid? parentId, string title, string content = "") =>
        files.CreateTextNodeAsync(userId, parentId, title, content);

    [Fact]
    public async Task Children_are_ordered_and_positions_are_dense()
    {
        var parent = await NewPageAsync(jess, null, "parent");
        var a = await NewPageAsync(jess, parent.Id, "a");
        var b = await NewPageAsync(jess, parent.Id, "b");
        var c = await NewPageAsync(jess, parent.Id, "c");

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
        var source = await NewPageAsync(jess, null, "source");
        var target = await NewPageAsync(jess, null, "target");
        var one = await NewPageAsync(jess, source.Id, "one");
        var two = await NewPageAsync(jess, source.Id, "two");
        var existing = await NewPageAsync(jess, target.Id, "existing");

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
        var parent = await NewPageAsync(jess, null, "parent");
        var a = await NewPageAsync(jess, parent.Id, "a");
        var b = await NewPageAsync(jess, parent.Id, "b");
        var c = await NewPageAsync(jess, parent.Id, "c");

        await nodes.MoveAsync(jess, c.Id, parent.Id, position: 0);

        var children = await nodes.GetChildrenAsync(jess, parent.Id);
        Assert.Equal([c.Id, a.Id, b.Id], children.Select(n => n.Id));
    }

    [Fact]
    public async Task A_node_cannot_move_into_its_own_subtree()
    {
        var root = await NewPageAsync(jess, null, "root");
        var child = await NewPageAsync(jess, root.Id, "child");
        var grandchild = await NewPageAsync(jess, child.Id, "grandchild");

        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.MoveAsync(jess, root.Id, grandchild.Id));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.MoveAsync(jess, root.Id, root.Id));
    }

    [Fact]
    public async Task Private_subtrees_hide_from_everyone_but_the_owner()
    {
        var secret = await NewPageAsync(jess, null, "secret");
        var inside = await NewPageAsync(jess, secret.Id, "inside");
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
        var secret = await NewPageAsync(jess, null, "secret");
        await nodes.SetPrivateAsync(jess, secret.Id, true);
        var wanderer = await NewPageAsync(sam, null, "wanderer");

        // Sam can't see jess's private node, so jess performs the move.
        await nodes.MoveAsync(jess, wanderer.Id, secret.Id);
        Assert.DoesNotContain(await nodes.GetTreeAsync(sam), n => n.Id == wanderer.Id);

        await nodes.MoveAsync(jess, wanderer.Id, null);
        Assert.Contains(await nodes.GetTreeAsync(sam), n => n.Id == wanderer.Id);
    }

    [Fact]
    public async Task Only_the_owner_can_toggle_privacy()
    {
        var page = await NewPageAsync(jess, null, "mine");

        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.SetPrivateAsync(sam, page.Id, true));
    }

    [Fact]
    public async Task Saving_markdown_with_mentions_creates_backlinks()
    {
        var target = await NewPageAsync(jess, null, "target");
        var source = await NewPageAsync(jess, null, "source");

        await files.SaveTextAsync(jess, source.Id, $"See [@target](node://{target.Id}).");

        var backlinks = await nodes.GetBacklinksAsync(jess, target.Id);
        Assert.Equal([source.Id], backlinks.Select(n => n.Id));

        await files.SaveTextAsync(jess, source.Id, "no more links");
        Assert.Empty(await nodes.GetBacklinksAsync(jess, target.Id));
    }

    [Fact]
    public async Task Tags_contribute_to_search_text_and_can_be_removed()
    {
        var page = await NewPageAsync(jess, null, "quadlets");
        await nodes.AddTagAsync(jess, page.Id, " Podman ");
        await nodes.AddTagAsync(jess, page.Id, "podman");

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Contains("podman", fresh.SearchText);
        Assert.Single(fresh.Tags);

        await nodes.RemoveTagAsync(jess, page.Id, "PODMAN");
        fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.DoesNotContain("podman", fresh.SearchText);
        Assert.DoesNotContain(await nodes.ListTagsAsync(jess), t => t.Name == "podman");
    }

    [Fact]
    public async Task Similar_ranks_a_link_above_shared_tags_and_hides_private_nodes()
    {
        var subject = await NewPageAsync(jess, null, "subject");
        var linkedAndTagged = await NewPageAsync(jess, null, "linked and tagged");
        var twoTags = await NewPageAsync(jess, null, "two tags");
        var oneTag = await NewPageAsync(jess, null, "one tag");
        await NewPageAsync(jess, null, "unrelated");
        var hidden = await NewPageAsync(sam, null, "hidden");

        foreach (var (owner, id) in new[]
            { (jess, subject.Id), (jess, linkedAndTagged.Id), (jess, twoTags.Id),
              (jess, oneTag.Id), (sam, hidden.Id) })
            await nodes.AddTagAsync(owner, id, "alpha");
        await nodes.AddTagAsync(jess, subject.Id, "beta");
        await nodes.AddTagAsync(jess, twoTags.Id, "beta");
        await files.SaveTextAsync(jess, subject.Id, $"See [@x](node://{linkedAndTagged.Id})");
        await nodes.SetPrivateAsync(sam, hidden.Id, true);

        var similar = await nodes.GetSimilarAsync(jess, subject.Id);

        // linked + one shared tag (3) beats two shared tags (2) beats one (1);
        // the untagged and the privately hidden nodes never appear.
        Assert.Equal([linkedAndTagged.Id, twoTags.Id, oneTag.Id], similar.Select(s => s.Id));
    }

    [Fact]
    public async Task Similar_counts_inbound_links_too()
    {
        var subject = await NewPageAsync(jess, null, "subject");
        var fan = await NewPageAsync(jess, null, "fan", $"[@s](node://{subject.Id})");

        var similar = await nodes.GetSimilarAsync(jess, subject.Id);

        Assert.Equal([fan.Id], similar.Select(s => s.Id));
    }

    [Fact]
    public async Task A_new_page_is_a_markdown_file_node()
    {
        var page = await NewPageAsync(jess, null, "My Notes", "# hello");

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(NodeKind.Page, fresh.Kind);
        Assert.Equal(MediaTypes.Markdown, fresh.MediaType);
        Assert.Equal("My Notes.md", fresh.File!.Current.FileName);
        Assert.Equal("# hello", fresh.File.Current.ExtractedText);
        Assert.Equal("# hello", await files.GetTextAsync(jess, page.Id));
    }
}
