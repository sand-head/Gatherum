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
    public async Task Nodes_are_private_until_somebody_is_named_and_then_the_subtree_follows()
    {
        var secret = await NewPageAsync(jess, null, "secret");
        var inside = await NewPageAsync(jess, secret.Id, "inside");

        // Nothing was declared, so nothing is shared: the default is closed.
        Assert.Equal(2, (await nodes.GetTreeAsync(jess)).Count);
        Assert.Empty(await nodes.GetTreeAsync(sam));
        await Assert.ThrowsAsync<Gatherum.Core.NotFoundException>(
            () => nodes.GetWithBodyAsync(sam, inside.Id));

        // Access is additive downward: naming Sam on the parent carries to the child.
        await harness.Access.GrantAsync(jess, secret.Id, sam, AccessRole.Reader);
        Assert.Equal(2, (await nodes.GetTreeAsync(sam)).Count);

        await harness.Access.RevokeAsync(jess, secret.Id, sam);
        Assert.Empty(await nodes.GetTreeAsync(sam));
    }

    [Fact]
    public async Task Moving_into_a_shared_subtree_inherits_the_share_and_leaving_it_ends()
    {
        var shared = await NewPageAsync(jess, null, "shared");
        await harness.Access.GrantAsync(jess, shared.Id, sam, AccessRole.Reader);
        var wanderer = await NewPageAsync(jess, null, "wanderer");
        Assert.DoesNotContain(await nodes.GetTreeAsync(sam), n => n.Id == wanderer.Id);

        await nodes.MoveAsync(jess, wanderer.Id, shared.Id);
        Assert.Contains(await nodes.GetTreeAsync(sam), n => n.Id == wanderer.Id);

        await nodes.MoveAsync(jess, wanderer.Id, null);
        Assert.DoesNotContain(await nodes.GetTreeAsync(sam), n => n.Id == wanderer.Id);
    }

    [Fact]
    public async Task A_subtree_can_refuse_what_the_directory_above_it_shared()
    {
        var shared = await NewPageAsync(jess, null, "shared");
        var tighter = await NewPageAsync(jess, shared.Id, "tighter");
        await harness.Access.GrantAsync(jess, shared.Id, sam, AccessRole.Reader);
        Assert.Equal(2, (await nodes.GetTreeAsync(sam)).Count);

        await harness.Access.SetAccessAsync(jess, tighter.Id, AccessMode.Private, inherit: false);

        Assert.Equal([shared.Id], (await nodes.GetTreeAsync(sam)).Select(n => n.Id));
    }

    [Fact]
    public async Task Public_is_the_internet_and_needs_no_user_at_all()
    {
        var page = await NewPageAsync(jess, null, "published");
        var draft = await NewPageAsync(jess, null, "draft");

        Assert.Empty(await nodes.GetTreeAsync(null));

        await harness.Access.SetAccessAsync(jess, page.Id, AccessMode.Public);

        Assert.Equal([page.Id], (await nodes.GetTreeAsync(null)).Select(n => n.Id));
        await Assert.ThrowsAsync<Gatherum.Core.NotFoundException>(
            () => nodes.GetWithBodyAsync(null, draft.Id));
    }

    [Fact]
    public async Task A_shared_node_reaches_the_other_users_tree_from_inside_its_owners()
    {
        // Ownership is the path, so a shared node stays in jess's subtree and shows up in
        // sam's listing with a parent sam cannot see. Anything drawing a tree by walking
        // down from a null parent would never reach it.
        var mine = await NewPageAsync(jess, null, "Homelab");
        var shared = await NewPageAsync(jess, mine.Id, "Podman");
        await harness.Access.GrantAsync(jess, shared.Id, sam, AccessRole.Reader);

        var samsTree = await nodes.GetTreeAsync(sam);
        var entry = Assert.Single(samsTree);
        Assert.Equal(shared.Id, entry.Id);
        Assert.False(entry.Owned);
        // Its parent is real and invisible, which is what the grouping has to cope with.
        Assert.Equal(mine.Id, entry.ParentId);
        Assert.DoesNotContain(samsTree, n => n.Id == mine.Id);

        Assert.All(await nodes.GetTreeAsync(jess), n => Assert.True(n.Owned));
    }

    [Fact]
    public async Task The_owner_can_list_take_back_and_change_what_they_granted()
    {
        var page = await NewPageAsync(jess, null, "Notes");
        await harness.Access.GrantAsync(jess, page.Id, sam, AccessRole.Reader);

        var granted = Assert.Single(await harness.Access.ListGrantsAsync(jess, page.Id));
        Assert.Equal(sam, granted.UserId);
        Assert.Equal(AccessRole.Reader, granted.Role);

        // A reader cannot write, and granting again is how the role is changed.
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => files.SaveTextAsync(sam, page.Id, "no"));
        await harness.Access.GrantAsync(jess, page.Id, sam, AccessRole.Editor);
        await files.SaveTextAsync(sam, page.Id, "yes");

        // Only the owner may read the list back.
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => harness.Access.ListGrantsAsync(sam, page.Id));

        await harness.Access.RevokeAsync(jess, page.Id, sam);
        Assert.Empty(await harness.Access.ListGrantsAsync(jess, page.Id));
        Assert.Empty(await nodes.GetTreeAsync(sam));
    }

    [Fact]
    public async Task An_editor_may_change_the_content_and_not_the_filing()
    {
        var page = await NewPageAsync(jess, null, "Notes");
        await harness.Access.GrantAsync(jess, page.Id, sam, AccessRole.Editor);

        // What an editor was given: the document.
        await files.SaveTextAsync(sam, page.Id, "rewritten by sam");
        await categories.AddAsync(sam, page.Id, "Homelab");

        // Not the filing cabinet. Ownership is the path, so these move files around
        // inside jess's directory.
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.RenameAsync(sam, page.Id, "sam's now"));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.DeleteAsync(sam, page.Id));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => nodes.MoveAsync(sam, page.Id, null));

        Assert.Equal("Notes", (await nodes.GetWithBodyAsync(jess, page.Id)).Title);
    }

    [Fact]
    public async Task A_reader_cannot_write_by_any_door()
    {
        var page = await NewPageAsync(jess, null, "Notes");
        await harness.Access.GrantAsync(jess, page.Id, sam, AccessRole.Reader);

        // Being able to see a node has never been the same as being allowed to change it.
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => files.SaveTextAsync(sam, page.Id, "no"));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => files.SetDescriptionAsync(sam, page.Id, "no"));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => files.RestoreVersionAsync(sam, page.Id, 1));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => categories.AddAsync(sam, page.Id, "Homelab"));

        // And reading still works, which is the whole of what they were given.
        Assert.Equal("Notes", (await nodes.GetWithBodyAsync(sam, page.Id)).Title);
    }

    [Fact]
    public async Task Unlisted_is_reachable_by_link_and_absent_from_every_listing()
    {
        var unlisted = await NewPageAsync(jess, null, "Half-written thoughts");
        await files.SaveTextAsync(jess, unlisted.Id, "the closet gets hot");
        var listed = await NewPageAsync(jess, null, "Published");
        await harness.Access.SetAccessAsync(jess, listed.Id, AccessMode.Public);
        await harness.Access.SetAccessAsync(jess, unlisted.Id, AccessMode.Unlisted);
        await harness.EmbedStaleAsync();

        // Reachable by anyone holding the id — signed in as somebody else, or nobody.
        Assert.Equal("Half-written thoughts", (await nodes.GetWithBodyAsync(sam, unlisted.Id)).Title);
        Assert.Equal("Half-written thoughts", (await nodes.GetWithBodyAsync(null, unlisted.Id)).Title);

        // And in no listing that would have handed the id out.
        Assert.Equal([listed.Id], (await nodes.GetTreeAsync(null)).Select(n => n.Id));
        Assert.DoesNotContain(await nodes.GetTreeAsync(sam), n => n.Id == unlisted.Id);
        Assert.DoesNotContain(await harness.Search.SearchAsync(null, "closet"),
            hit => hit.Id == unlisted.Id);
        Assert.DoesNotContain(await harness.Search.SearchAsync(sam, "closet"),
            hit => hit.Id == unlisted.Id);
        Assert.DoesNotContain("Half-written thoughts",
            (await nodes.ResolveTitlesAsync(null, ["Half-written thoughts"])).Keys);

        // Its owner still sees it everywhere, which is the point of the distinction:
        // unlisted hides it from people who do not already have access another way.
        Assert.Contains(await nodes.GetTreeAsync(jess), n => n.Id == unlisted.Id);
        Assert.Contains(await harness.Search.SearchAsync(jess, "closet"),
            hit => hit.Id == unlisted.Id);
    }

    [Fact]
    public async Task An_unlisted_child_of_a_published_directory_is_still_published()
    {
        // Reach is additive downward like grants are, so declaring a child *less* open
        // than what contains it does nothing on its own — inherit:false is that gesture.
        var directory = await NewPageAsync(jess, null, "Published");
        var inside = await NewPageAsync(jess, directory.Id, "Inside");
        await harness.Access.SetAccessAsync(jess, directory.Id, AccessMode.Public);
        await harness.Access.SetAccessAsync(jess, inside.Id, AccessMode.Unlisted);

        Assert.Contains(await nodes.GetTreeAsync(null), n => n.Id == inside.Id);

        await harness.Access.SetAccessAsync(jess, inside.Id, AccessMode.Unlisted, inherit: false);

        Assert.DoesNotContain(await nodes.GetTreeAsync(null), n => n.Id == inside.Id);
        Assert.Equal("Inside", (await nodes.GetWithBodyAsync(null, inside.Id)).Title);
    }

    [Fact]
    public async Task Turning_public_sharing_off_hides_public_nodes_at_once()
    {
        var page = await NewPageAsync(jess, null, "published");
        await harness.Access.SetAccessAsync(jess, page.Id, AccessMode.Public);
        Assert.Single(await nodes.GetTreeAsync(null));

        harness.Settings.Value.Sharing.AllowPublic = false;


        // Immediate, and without rewriting anything the owner recorded: the node still
        // says it is public, and nothing anonymous can reach it.
        Assert.Empty(await nodes.GetTreeAsync(null));
        await Assert.ThrowsAsync<Gatherum.Core.NotFoundException>(
            () => nodes.GetWithBodyAsync(null, page.Id));
        Assert.Equal(AccessMode.Public,
            (await nodes.GetWithBodyAsync(jess, page.Id)).Access);

        harness.Settings.Value.Sharing.AllowPublic = true;
        Assert.Single(await nodes.GetTreeAsync(null));
    }

    [Fact]
    public async Task Only_the_owner_can_change_who_may_reach_a_node()
    {
        var page = await NewPageAsync(jess, null, "mine");

        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => harness.Access.SetAccessAsync(sam, page.Id, AccessMode.Public));
        await Assert.ThrowsAsync<Gatherum.Core.ForbiddenException>(
            () => harness.Access.GrantAsync(sam, page.Id, sam, AccessRole.Editor));
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
        var page = await NewPageAsync(jess, null, "page");

        await files.SaveTextAsync(jess, page.Id, "See [[Sam's notes]].");

        Assert.Empty(await nodes.GetBacklinksAsync(sam, hidden.Id));
    }

    [Fact]
    public async Task A_page_asks_which_of_the_nodes_it_links_the_reader_may_open()
    {
        var published = await NewPageAsync(jess, null, "Published");
        var unlisted = await NewPageAsync(jess, null, "Unlisted");
        var shared = await NewPageAsync(jess, null, "Shared");
        var mine = await NewPageAsync(jess, null, "Mine alone");
        await harness.Access.SetAccessAsync(jess, published.Id, AccessMode.Public);
        await harness.Access.SetAccessAsync(jess, unlisted.Id, AccessMode.Unlisted);
        await harness.Access.GrantAsync(jess, shared.Id, sam, AccessRole.Reader);
        var nothing = Guid.NewGuid();

        var links = new List<Guid> { published.Id, unlisted.Id, shared.Id, mine.Id, nothing };

        async Task<List<Guid>> Reachable(Guid? viewer) =>
            [.. (await nodes.ReachableIdsAsync(viewer, links)).OrderBy(links.IndexOf)];

        // A link is the direct-reach question, so unlisted answers it — the id in the
        // page is what stands in for permission. Private to its author stays private,
        // and an id naming nothing is unreachable rather than an error.
        Assert.Equal([published.Id, unlisted.Id], await Reachable(null));
        Assert.Equal([published.Id, unlisted.Id, shared.Id], await Reachable(sam));
        Assert.Equal([published.Id, unlisted.Id, shared.Id, mine.Id], await Reachable(jess));
        Assert.Empty(await nodes.ReachableIdsAsync(jess, []));
    }

    [Fact]
    public async Task Titles_resolve_by_name_ignoring_case_and_hiding_the_private()
    {
        var homelab = await NewPageAsync(jess, null, "Homelab");
        var mine = await NewPageAsync(sam, null, "Sam's notes");

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
        await harness.FileUnderAsync(jess, subject.Id, "Podman", nestedUnder: "Homelab");
        await categories.AddAsync(jess, sibling.Id, "Podman");
        await harness.FileUnderAsync(jess, cousin.Id, "Backups", nestedUnder: "Homelab");

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
