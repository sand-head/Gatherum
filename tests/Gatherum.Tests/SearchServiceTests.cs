using System.Text;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class SearchServiceTests(PostgresFixture postgres) : IAsyncLifetime
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

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task Finds_pages_by_body_text_with_kind_and_snippet()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Deployment notes",
            "Rootless Podman quadlets restart cleanly after reboot.");

        var results = await harness.Search.SearchAsync(jess, "quadlets");

        var hit = Assert.Single(results);
        Assert.Equal(page.Id, hit.Id);
        Assert.Equal(NodeKind.Page, hit.Kind);
        Assert.Contains("quadlets", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Title_matches_rank_above_body_matches()
    {
        await harness.Files.CreateTextNodeAsync(jess, null, "Random notes",
            "something about kestrel");
        var titleMatch = await harness.Files.CreateTextNodeAsync(jess, null, "Kestrel tuning");

        var results = await harness.Search.SearchAsync(jess, "kestrel");

        Assert.Equal(titleMatch.Id, results.First().Id);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Categories_are_searchable_by_every_name_they_are_nested_under()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Chapter 3");
        await harness.Categories.AddAsync(jess, page.Id, "Fiction/Worldbuilding");

        Assert.Equal([page.Id],
            (await harness.Search.SearchAsync(jess, "worldbuilding")).Select(r => r.Id));
        Assert.Equal([page.Id],
            (await harness.Search.SearchAsync(jess, "fiction")).Select(r => r.Id));
    }

    [Fact]
    public async Task Uploaded_file_text_is_searchable_and_kind_filters_split_pages_from_files()
    {
        await harness.Files.CreateTextNodeAsync(jess, null, "ferret care page", "about ferrets");
        var upload = new MemoryStream(Encoding.UTF8.GetBytes("ferret feeding schedule"));
        await harness.Files.CreateFileNodeAsync(jess, null, "schedule.txt", "text/plain", upload);

        Assert.Equal(2, (await harness.Search.SearchAsync(jess, "ferret")).Count);
        Assert.Single(await harness.Search.SearchAsync(jess, "ferret", NodeKind.Page));
        Assert.Single(await harness.Search.SearchAsync(jess, "ferret", NodeKind.File));
    }

    [Fact]
    public async Task Private_nodes_never_leak_into_other_users_results()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "hidden treasure map");

        Assert.Single(await harness.Search.SearchAsync(jess, "treasure"));
        Assert.Empty(await harness.Search.SearchAsync(sam, "treasure"));
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
}
