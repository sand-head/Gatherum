using Gatherum.Core;
using Gatherum.Core.Domain;
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
    public async Task Filing_a_page_writes_the_category_a_page_of_its_own()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");

        var name = await categories.AddAsync(jess, page.Id, "  Home  lab ");

        Assert.Equal("Home lab", name);
        var category = await categories.ResolveAsync("home lab");
        Assert.NotNull(category);
        Assert.True(category.IsCategory);
        Assert.Equal(NodeKind.Category, category.Kind);
        // It is a page, so it is a Markdown file at a readable path, with a body waiting
        // for somebody to say what belongs in it.
        Assert.Equal(MediaTypes.Markdown, category.MediaType);
        Assert.Equal("Categories/Home lab.md", category.RelativePath);
        Assert.Equal("", await files.GetTextAsync(jess, category.Id));

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Contains("home lab", fresh.SearchText);
    }

    [Fact]
    public async Task The_same_category_written_differently_is_the_same_category()
    {
        var first = await files.CreateTextNodeAsync(jess, null, "first");
        var second = await files.CreateTextNodeAsync(jess, null, "second");

        await categories.AddAsync(jess, first.Id, "Podman");
        await categories.AddAsync(jess, second.Id, "  podman ");
        await categories.AddAsync(jess, second.Id, "PODMAN");

        var podman = (await categories.ListAsync(jess)).Single(c => c.Name == "Podman");
        Assert.Equal(2, podman.Members);
        Assert.Single((await harness.ReloadAsync(jess, second.Id)).Categories);
    }

    [Fact]
    public async Task Nesting_a_category_is_filing_it_under_another()
    {
        var overview = await files.CreateTextNodeAsync(jess, null, "Homelab overview");
        var quadlets = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, overview.Id, "Homelab");
        var podman = await harness.FileUnderAsync(jess, quadlets.Id, "Podman",
            nestedUnder: "Homelab");

        var homelab = (await categories.ListAsync(jess)).Single(c => c.Name == "Homelab");
        // A subcategory is listed as one, never counted as a member of its parent.
        Assert.Equal(1, homelab.Members);
        Assert.Equal(2, homelab.SubtreeMembers);
        Assert.Equal([homelab.Id],
            (await categories.ListAsync(jess)).Single(c => c.Id == podman.Id).ParentIds);

        var shallow = await categories.GetAsync(jess, "Homelab");
        Assert.Equal([overview.Id], shallow.Nodes.Select(n => n.Id));
        Assert.Equal(["Podman"], shallow.Subcategories.Select(c => c.Name));
        Assert.Empty(shallow.Parents);

        var deep = await categories.GetAsync(jess, "Homelab", deep: true);
        // Ordered by title: "Homelab overview" before "Quadlets".
        Assert.Equal([overview.Id, quadlets.Id], deep.Nodes.Select(n => n.Id));

        // And the whole ancestry is in the member's search text, so the parent finds it.
        Assert.Contains("homelab podman", (await harness.ReloadAsync(jess, quadlets.Id)).SearchText);
    }

    [Fact]
    public async Task A_subject_can_sit_under_two_parents_at_once()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        var podman = await harness.FileUnderAsync(jess, page.Id, "Podman",
            nestedUnder: "Homelab");
        await categories.AddAsync(jess, podman.Id, "Containers");

        var all = await categories.ListAsync(jess);
        Assert.Equal(2, all.Single(c => c.Name == "Podman").ParentIds.Count);
        Assert.Equal(1, all.Single(c => c.Name == "Homelab").SubtreeMembers);
        Assert.Equal(1, all.Single(c => c.Name == "Containers").SubtreeMembers);

        // Both names reach the page, which is the thing a path could never say.
        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Contains("containers", fresh.SearchText);
        Assert.Contains("homelab", fresh.SearchText);
    }

    [Fact]
    public async Task A_category_cannot_be_nested_inside_itself()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        var podman = await harness.FileUnderAsync(jess, page.Id, "Podman",
            nestedUnder: "Homelab");
        var homelab = await categories.ResolveAsync("Homelab");

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            categories.AddAsync(jess, podman.Id, "Podman"));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            categories.AddAsync(jess, homelab!.Id, "Podman"));
    }

    [Fact]
    public async Task A_node_in_a_category_and_its_subcategory_is_counted_once()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab");
        await harness.FileUnderAsync(jess, page.Id, "Podman", nestedUnder: "Homelab");

        var homelab = (await categories.ListAsync(jess)).Single(c => c.Name == "Homelab");
        Assert.Equal(1, homelab.SubtreeMembers);
    }

    [Fact]
    public async Task Leaving_a_category_leaves_the_ones_it_is_nested_under_alone()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await categories.AddAsync(jess, page.Id, "Homelab");
        await harness.FileUnderAsync(jess, page.Id, "Podman", nestedUnder: "Homelab");

        await categories.RemoveAsync(jess, page.Id, "podman");

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(["Homelab"], fresh.Categories.Select(c => c.Category!.Title));
        Assert.Contains("homelab", fresh.SearchText);
        Assert.DoesNotContain("podman", fresh.SearchText);
    }

    [Fact]
    public async Task Renaming_a_category_is_renaming_its_page_and_carries_everything_with_it()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await harness.FileUnderAsync(jess, page.Id, "Podman", nestedUnder: "Homelab");
        var homelab = await categories.ResolveAsync("Homelab");

        // No category rename verb: it is a node, so this is the node rename.
        await nodes.RenameAsync(jess, homelab!.Id, "Home lab");

        Assert.Null(await categories.ResolveAsync("Homelab"));
        Assert.NotNull(await categories.ResolveAsync("home lab"));
        Assert.Contains("home lab podman", (await harness.ReloadAsync(jess, page.Id)).SearchText);

        // The name is what the members write down on disk, so the sidecar of everything
        // filed directly in it has to have followed — or the next reindex would file
        // them under a category nothing is called any more.
        var podman = await categories.ResolveAsync("Podman");
        var written = await harness.Metadata.ReadAsync(await harness.PathOfAsync(podman!.Id));
        Assert.Equal(["Home lab"], written!.Categories);
    }

    [Fact]
    public async Task Deleting_a_category_frees_its_pages_and_promotes_its_subcategories()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await harness.FileUnderAsync(jess, page.Id, "Podman", nestedUnder: "Homelab");
        await categories.AddAsync(jess, page.Id, "Fiction");
        var homelab = await categories.ResolveAsync("Homelab");

        await nodes.DeleteAsync(jess, homelab!.Id);

        // Podman is a page, so deleting the category above it does not delete it — it
        // stops being nested and becomes a subject of its own.
        var all = await categories.ListAsync(jess);
        Assert.Equal(["Fiction", "Podman"], all.Select(c => c.Name).Order());
        Assert.Empty(all.Single(c => c.Name == "Podman").ParentIds);

        var fresh = await harness.ReloadAsync(jess, page.Id);
        Assert.Equal(["Fiction", "Podman"], fresh.Categories.Select(c => c.Category!.Title).Order());
        Assert.DoesNotContain("homelab", fresh.SearchText);
    }

    [Fact]
    public async Task A_category_holding_only_the_other_users_private_pages_is_not_listed()
    {
        var secret = await files.CreateTextNodeAsync(sam, null, "Surprise party");
        await harness.FileUnderAsync(sam, secret.Id, "Birthday", nestedUnder: "Plans");

        Assert.Empty(await categories.ListAsync(jess));
        await Assert.ThrowsAsync<NotFoundException>(() => categories.GetAsync(jess, "plans"));

        var mine = await categories.ListAsync(sam);
        Assert.Equal(["Birthday", "Plans"], mine.Select(c => c.Name));
    }

    [Fact]
    public async Task An_empty_category_stays_and_a_nameless_one_is_refused()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        await harness.FileUnderAsync(jess, page.Id, "Podman", nestedUnder: "Homelab");
        await categories.RemoveAsync(jess, page.Id, "Podman");

        Assert.Equal(["Homelab", "Podman"], (await categories.ListAsync(jess)).Select(c => c.Name));

        await Assert.ThrowsAsync<ValidationException>(() =>
            categories.AddAsync(jess, page.Id, "   "));
        await Assert.ThrowsAsync<ValidationException>(() =>
            categories.AddAsync(jess, page.Id, new string('x', CategoryName.MaxLength + 1)));
    }

    [Fact]
    public async Task A_wiki_link_resolves_to_a_category_because_it_is_a_page()
    {
        var page = await files.CreateTextNodeAsync(jess, null, "Quadlets");
        var podman = await harness.FileUnderAsync(jess, page.Id, "Podman");

        var resolved = await nodes.ResolveTitlesAsync(jess, ["podman"]);

        Assert.Equal(podman.Id, resolved["podman"]);
        // Which means it is a real backlink target too: the subject can be mentioned.
        await files.SaveTextAsync(jess, page.Id, $"See [@Podman](node://{podman.Id}).");
        Assert.Equal([page.Id],
            (await nodes.GetBacklinksAsync(jess, podman.Id)).Select(n => n.Id));
    }

    [Fact]
    public async Task Filing_a_node_nobody_can_see_is_a_not_found()
    {
        var secret = await files.CreateTextNodeAsync(sam, null, "Surprise party");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            categories.AddAsync(jess, secret.Id, "Plans"));
    }
}
