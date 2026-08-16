using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class SearchServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private GatherumDbContext db = null!;
    private NodeService nodes = null!;
    private SearchService search = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        db = PostgresFixture.CreateContext(await postgres.CreateDatabaseAsync());
        var authorizer = new DefaultNodeAuthorizer();
        nodes = new NodeService(db, authorizer, TimeProvider.System);
        search = new SearchService(db, authorizer);
        jess = await AddUserAsync("jess");
        sam = await AddUserAsync("sam");
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    [Fact]
    public async Task Finds_pages_by_body_text_with_kind_and_snippet()
    {
        var page = await nodes.CreatePageAsync(jess, null, "Deployment notes");
        await nodes.SavePageAsync(jess, page.Id,
            PageMarkdown.ToDocJson("Rootless Podman quadlets restart cleanly after reboot."));

        var results = await search.SearchAsync(jess, "quadlets");

        var hit = Assert.Single(results);
        Assert.Equal(page.Id, hit.Id);
        Assert.Equal(NodeKind.Page, hit.Kind);
        Assert.Contains("quadlets", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Title_matches_rank_above_body_matches()
    {
        var bodyMatch = await nodes.CreatePageAsync(jess, null, "Random notes");
        await nodes.SavePageAsync(jess, bodyMatch.Id, PageMarkdown.ToDocJson("something about kestrel"));
        var titleMatch = await nodes.CreatePageAsync(jess, null, "Kestrel tuning");

        var results = await search.SearchAsync(jess, "kestrel");

        Assert.Equal(titleMatch.Id, results.First().Id);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Tags_are_searchable()
    {
        var page = await nodes.CreatePageAsync(jess, null, "Chapter 3");
        await nodes.AddTagAsync(jess, page.Id, "worldbuilding");

        var results = await search.SearchAsync(jess, "worldbuilding");

        Assert.Equal([page.Id], results.Select(r => r.Id));
    }

    [Fact]
    public async Task Kind_filter_narrows_results()
    {
        var page = await nodes.CreatePageAsync(jess, null, "ferret care");

        Assert.Single(await search.SearchAsync(jess, "ferret", NodeKind.Page));
        Assert.Empty(await search.SearchAsync(jess, "ferret", NodeKind.File));
    }

    [Fact]
    public async Task Private_nodes_never_leak_into_other_users_results()
    {
        var page = await nodes.CreatePageAsync(jess, null, "hidden treasure map");
        await nodes.SetPrivateAsync(jess, page.Id, true);

        Assert.Single(await search.SearchAsync(jess, "treasure"));
        Assert.Empty(await search.SearchAsync(sam, "treasure"));
    }

    [Fact]
    public void Snippet_centers_on_the_first_hit()
    {
        var text = string.Concat(Enumerable.Repeat("padding ", 50)) + "needle in the middle "
            + string.Concat(Enumerable.Repeat("padding ", 50));

        var snippet = SearchService.Snippet(text, "needle");

        Assert.Contains("needle in the middle", snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.True(snippet.Length < 220);
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
