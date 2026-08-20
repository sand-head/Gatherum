using Gatherum.Core.Domain;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class NodeServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private NodeService nodes = null!;
    private CategoryService categories = null!;
    private FileService files = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        nodes = harness.Nodes;
        categories = harness.Categories;
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
    public async Task Wiki_links_name_a_page_and_become_backlinks()
    {
        var target = await NewPageAsync(jess, null, "Homelab");
        var source = await NewPageAsync(jess, null, "source");

        await files.SaveTextAsync(jess, source.Id, "Racked in [[homelab|the closet]].");

        var backlinks = await nodes.GetBacklinksAsync(jess, target.Id);
        Assert.Equal([source.Id], backlinks.Select(n => n.Id));

        await files.SaveTextAsync(jess, source.Id, "Racked in `[[Homelab]]`, in code.");
        Assert.Empty(await nodes.GetBacklinksAsync(jess, target.Id));
    }

    [Fact]
    public async Task A_wiki_link_only_resolves_to_what_its_writer_can_see()
    {
        var hidden = await NewPageAsync(sam, null, "Sam's notes");
        await nodes.SetPrivateAsync(sam, hidden.Id, true);
        var page = await NewPageAsync(jess, null, "page");

        await files.SaveTextAsync(jess, page.Id, "See [[Sam's notes]].");

        Assert.Empty(await nodes.GetBacklinksAsync(sam, hidden.Id));
    }

    [Fact]
    public async Task Titles_resolve_by_name_ignoring_case_and_hiding_the_private()
    {
        var homelab = await NewPageAsync(jess, null, "Homelab");
        var mine = await NewPageAsync(sam, null, "Sam's notes");
        await nodes.SetPrivateAsync(sam, mine.Id, true);

        var resolved = await nodes.ResolveTitlesAsync(jess,
            ["  homelab  ", "Sam's notes", "nothing by this name"]);

        Assert.Equal(homelab.Id, resolved["HOMELAB"]);
        Assert.DoesNotContain("Sam's notes", resolved.Keys);
        Assert.Single(resolved);
    }

    [Fact]
    public async Task The_same_title_twice_resolves_to_the_older_node()
    {
        var first = await NewPageAsync(jess, null, "Notes");
        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await NewPageAsync(jess, null, "notes");

        var resolved = await nodes.ResolveTitlesAsync(jess, ["Notes"]);

        Assert.Equal(first.Id, resolved["Notes"]);
    }

    [Fact]
    public async Task Similar_ranks_a_link_above_a_shared_category_and_hides_private_nodes()
    {
        var subject = await NewPageAsync(jess, null, "subject");
        var linkedAndFiled = await NewPageAsync(jess, null, "linked and filed");
        var twoCategories = await NewPageAsync(jess, null, "two categories");
        var oneCategory = await NewPageAsync(jess, null, "one category");
        await NewPageAsync(jess, null, "unrelated");
        var hidden = await NewPageAsync(sam, null, "hidden");

        foreach (var (owner, id) in new[]
            { (jess, subject.Id), (jess, linkedAndFiled.Id), (jess, twoCategories.Id),
              (jess, oneCategory.Id), (sam, hidden.Id) })
            await categories.AddAsync(owner, id, "Alpha");
        await categories.AddAsync(jess, subject.Id, "Beta");
        await categories.AddAsync(jess, twoCategories.Id, "Beta");
        await files.SaveTextAsync(jess, subject.Id, $"See [@x](node://{linkedAndFiled.Id})");
        await nodes.SetPrivateAsync(sam, hidden.Id, true);

        var similar = await nodes.GetSimilarAsync(jess, subject.Id);

        // linked + one shared category (6) beats two shared (4) beats one (2); the
        // uncategorized and the privately hidden nodes never appear.
        Assert.Equal([linkedAndFiled.Id, twoCategories.Id, oneCategory.Id],
            similar.Select(s => s.Id));
    }

    [Fact]
    public async Task Similar_prefers_the_same_category_to_a_shared_ancestor()
    {
        var subject = await NewPageAsync(jess, null, "subject");
        var sibling = await NewPageAsync(jess, null, "sibling");
        var cousin = await NewPageAsync(jess, null, "cousin");
        await categories.AddAsync(jess, subject.Id, "Homelab/Podman");
        await categories.AddAsync(jess, sibling.Id, "Homelab/Podman");
        await categories.AddAsync(jess, cousin.Id, "Homelab/Backups");

        var similar = await nodes.GetSimilarAsync(jess, subject.Id);

        Assert.Equal([sibling.Id, cousin.Id], similar.Select(s => s.Id));
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
