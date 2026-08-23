using Gatherum.Core;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class CategoryServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private CategoryService categories = null!;
    private NodeService nodes = null!;
    private FileService files = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        categories = harness.Categories;
        nodes = harness.Nodes;
        files = harness.Files;
        jess = await harness.AddUserAsync("jess");
        sam = await harness.AddUserAsync("sam");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task Filing_a_page_creates_the_categories_it_is_nested_under()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");

        var path = await categories.AddAsync(jess, page.Id, " Homelab / Podman ");

        Assert.Equal("homelab/podman", path);
        var all = await categories.ListAsync(jess);
        Assert.Equal(["homelab", "homelab/podman"], all.Select(c => c.Path));
        Assert.Equal([null, "homelab"], all.Select(c => c.ParentPath));
        Assert.Equal(["Homelab", "Podman"], all.Select(c => c.Name));

        // The whole ancestry is in the search text, so the parent finds the child's page.
        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Contains("homelab podman", fresh.SearchText);
    }

    [Fact]
    public async Task The_same_category_written_differently_is_the_same_category()
    {
        var first = await files.CreateTextNodeAsync(jess, null, "first");
        var second = await files.CreateTextNodeAsync(jess, null, "second");

        await categories.AddAsync(jess, first.Id, "Homelab/Podman");
        await categories.AddAsync(jess, second.Id, "homelab / PODMAN");
        await categories.AddAsync(jess, second.Id, "Homelab/Podman");

        var podman = (await categories.ListAsync(jess)).Single(c => c.Path == "homelab/podman");
        Assert.Equal(2, podman.Members);
        Assert.Single((await harness.ReloadAsync(jess, second.Id)).Categories);
    }

    [Fact]
    public async Task A_category_holds_what_its_subcategories_hold()
    {
        var overview = await files.CreateTextNodeAsync(jess, null, "Homelab overview");
        var quadlets = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, overview.Id, "Homelab");
        await categories.AddAsync(jess, quadlets.Id, "Homelab/Podman");

        var homelab = (await categories.ListAsync(jess)).Single(c => c.Path == "homelab");
        Assert.Equal(1, homelab.Members);
        Assert.Equal(2, homelab.SubtreeMembers);

        var shallow = await categories.GetAsync(jess, "Homelab");
        Assert.Equal([overview.Id], shallow.Nodes.Select(n => n.Id));
        Assert.Equal(["homelab/podman"], shallow.Subcategories.Select(c => c.Path));

        var deep = await categories.GetAsync(jess, "Homelab", deep: true);
        // Ordered by title: "Homelab overview" before "Quadlets".
        Assert.Equal([overview.Id, quadlets.Id], deep.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task A_node_in_a_category_and_its_subcategory_is_counted_once()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab");
        await categories.AddAsync(jess, page.Id, "Homelab/Podman");

        var homelab = (await categories.ListAsync(jess)).Single(c => c.Path == "homelab");
        Assert.Equal(1, homelab.SubtreeMembers);
    }

    [Fact]
    public async Task Leaving_a_category_leaves_the_ones_it_is_nested_under_alone()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab");
        await categories.AddAsync(jess, page.Id, "Homelab/Podman");

        await categories.RemoveAsync(jess, page.Id, "homelab/podman");

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(["homelab"], fresh.Categories.Select(c => c.Category!.Path));
        Assert.Contains("homelab", fresh.SearchText);
    }

    [Fact]
    public async Task Renaming_a_category_carries_its_subcategories_and_their_search_text()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab/Podman");

        await categories.RenameAsync("Homelab", "Home lab");

        var all = await categories.ListAsync(jess);
        Assert.Equal(["home lab", "home lab/podman"], all.Select(c => c.Path));
        Assert.Contains("home lab podman", (await harness.ReloadAsync(jess, page.Id)).SearchText);
    }

    [Fact]
    public async Task Moving_a_category_renests_it_and_refuses_its_own_subtree()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Podman/Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab");

        await categories.MoveAsync("Podman", "Homelab");

        var all = await categories.ListAsync(jess);
        Assert.Equal(["homelab", "homelab/podman", "homelab/podman/quadlets"],
            all.Select(c => c.Path));
        Assert.Contains("homelab podman quadlets",
            (await harness.ReloadAsync(jess, page.Id)).SearchText);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            categories.MoveAsync("homelab", "homelab/podman"));
    }

    [Fact]
    public async Task Deleting_a_category_takes_its_subcategories_and_leaves_the_nodes()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab/Podman");
        await categories.AddAsync(jess, page.Id, "Fiction");

        await categories.DeleteAsync("Homelab");

        Assert.Equal(["fiction"], (await categories.ListAsync(jess)).Select(c => c.Path));
        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(["fiction"], fresh.Categories.Select(c => c.Category!.Path));
        Assert.DoesNotContain("podman", fresh.SearchText);
    }

    [Fact]
    public async Task A_category_holding_only_the_other_users_private_pages_is_not_listed()
    {
        var secret = await files.CreateTextNodeAsync(sam, null, "Surprise party");
        await categories.AddAsync(sam, secret.Id, "Plans/Birthday");

        Assert.Empty(await categories.ListAsync(jess));
        await Assert.ThrowsAsync<NotFoundException>(() => categories.GetAsync(jess, "plans"));

        var mine = await categories.ListAsync(sam);
        Assert.Equal(["plans", "plans/birthday"], mine.Select(c => c.Path));
    }

    [Fact]
    public async Task An_empty_category_stays_and_a_nameless_one_is_refused()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab/Podman");
        await categories.RemoveAsync(jess, page.Id, "Homelab/Podman");

        Assert.Equal(["homelab", "homelab/podman"],
            (await categories.ListAsync(jess)).Select(c => c.Path));

        await Assert.ThrowsAsync<ValidationException>(() =>
            categories.AddAsync(jess, page.Id, " / / "));
        await Assert.ThrowsAsync<ValidationException>(() =>
            categories.AddAsync(jess, page.Id, string.Join('/', Enumerable.Repeat("deep", 9))));
    }

    [Fact]
    public async Task Filing_a_node_nobody_can_see_is_a_not_found()
    {
        var secret = await files.CreateTextNodeAsync(sam, null, "Surprise party");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            categories.AddAsync(jess, secret.Id, "Plans"));
    }
}
