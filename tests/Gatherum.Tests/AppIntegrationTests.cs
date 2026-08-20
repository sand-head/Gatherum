using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gatherum.Tests;

/// <summary>Boots the real app against Postgres and walks the promised path:
/// create a page over REST, find it via search, read it back through MCP.</summary>
[Collection("postgres")]
public class AppIntegrationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private string storageRoot = "";
    private readonly FakeEmbedder embedder = new();

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        storageRoot = Path.Combine(Path.GetTempPath(), $"gatherum-it-{Guid.NewGuid():N}");
        embedder.Means("cooling", "thermals", "overheating", "noisy");
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gatherum:Database:ConnectionString", connectionString);
            builder.UseSetting("Gatherum:Storage:Root", storageRoot);
            // Configured so the app wires embeddings up at all; the endpoint is never
            // reached because the embedder behind it is replaced below.
            builder.UseSetting("Gatherum:Embedding:Endpoint", "http://embedder.invalid");
            builder.UseSetting("Gatherum:Embedding:Model", "fake-embed");
            builder.UseSetting("Gatherum:Embedding:Dimensions",
                PostgresFixture.EmbeddingDimensions.ToString());
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmbedder>();
                services.AddSingleton<IEmbedder>(embedder);
            });
        });

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var user = await users.GetOrCreateAsync("it-user", "it@example.org", "Integration");
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var created = await keys.CreateAsync(user.Id, "integration");

        client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created.PlaintextToken);
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        if (Directory.Exists(storageRoot))
            Directory.Delete(storageRoot, recursive: true);
    }

    [Fact]
    public async Task Create_page_search_it_and_read_it_back_over_mcp()
    {
        var markdown = "# Deploy notes\n\nRootless **quadlets** restart after reboot.";
        var create = await client.PostAsJsonAsync("/api/pages",
            new { title = "Deploy notes", markdown });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var page = await create.Content.ReadFromJsonAsync<JsonElement>();
        var pageId = page.GetProperty("id").GetGuid();

        var results = await client.GetFromJsonAsync<JsonElement>("/api/search?query=quadlets");
        Assert.Contains(results.EnumerateArray(),
            r => r.GetProperty("id").GetGuid() == pageId);

        var node = await CallMcpToolAsync("get_node", new { id = pageId });
        Assert.Equal("Deploy notes", node.GetProperty("title").GetString());
        Assert.Equal(markdown, node.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task A_wiki_link_resolves_by_title_and_backlinks_the_page_it_names()
    {
        var target = await client.PostAsJsonAsync("/api/pages",
            new { title = "Homelab", markdown = "The rack, the pi, the noise." });
        var targetId = (await target.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var source = await client.PostAsJsonAsync("/api/pages",
            new
            {
                title = "Quadlet notes",
                markdown = """
                    :::infobox
                    # Quadlet notes
                    | Runs on | [[Homelab]] |
                    :::

                    Rootless units restart after reboot.
                    """,
            });
        var sourceId = (await source.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var backlinks = await client.GetFromJsonAsync<JsonElement>(
            $"/api/nodes/{targetId}/backlinks");
        Assert.Contains(backlinks.EnumerateArray(),
            b => b.GetProperty("id").GetGuid() == sourceId);

        var resolve = await client.PostAsJsonAsync("/api/nodes/resolve-titles",
            new { titles = new[] { "homelab", "no such page" } });
        var matches = await resolve.Content.ReadFromJsonAsync<JsonElement>();
        var match = Assert.Single(matches.EnumerateArray());
        Assert.Equal(targetId, match.GetProperty("id").GetGuid());

        // The fence is the page's own text on the way back out, byte for byte.
        var node = await CallMcpToolAsync("get_node", new { id = sourceId });
        Assert.Contains(":::infobox", node.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task A_page_filed_in_a_nested_category_is_found_from_the_category_above()
    {
        var create = await client.PostAsJsonAsync("/api/pages",
            new { title = "Quadlet notes", markdown = "Rootless units restart after reboot." });
        var pageId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        var filed = await client.PostAsJsonAsync($"/api/nodes/{pageId}/categories",
            new { path = "Homelab/Podman" });
        filed.EnsureSuccessStatusCode();
        Assert.Equal("homelab/podman",
            (await filed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString());

        // The parent category holds it only when asked to look into its subcategories.
        var shallow = await client.GetFromJsonAsync<JsonElement>("/api/categories/homelab");
        Assert.Empty(shallow.GetProperty("nodes").EnumerateArray());
        Assert.Equal("homelab/podman",
            shallow.GetProperty("subcategories")[0].GetProperty("path").GetString());

        var deep = await client.GetFromJsonAsync<JsonElement>("/api/categories/homelab?deep=true");
        Assert.Contains(deep.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("id").GetGuid() == pageId);

        // Both names it is nested under are searchable, and MCP sees the same taxonomy.
        var results = await client.GetFromJsonAsync<JsonElement>("/api/search?query=homelab");
        Assert.Contains(results.EnumerateArray(), r => r.GetProperty("id").GetGuid() == pageId);

        var browsed = await CallMcpToolAsync("browse_category",
            new { path = "Homelab", deep = true });
        Assert.Contains(browsed.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("id").GetGuid() == pageId);
    }

    [Fact]
    public async Task The_health_endpoint_answers_without_auth()
    {
        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_and_mcp_reject_missing_keys()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/nodes/tree")).StatusCode);
        var mcp = await anonymous.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" });
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
    }

    private async Task<JsonElement> CallMcpToolAsync(string tool, object arguments)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Content = JsonContent.Create(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments },
        });
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal));
        var envelope = JsonDocument.Parse(data["data: ".Length..]).RootElement;
        var text = envelope.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task A_page_is_found_over_REST_by_a_question_it_never_uses_the_words_of()
    {
        var create = await client.PostAsJsonAsync("/api/pages", new
        {
            title = "Rack notes",
            markdown = "The overheating started after the third drive went in.",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var pageId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        await EmbedEverythingAsync();

        var semantic = await client.GetFromJsonAsync<JsonElement>(
            "/api/search?query=noisy&mode=semantic");
        var literal = await client.GetFromJsonAsync<JsonElement>(
            "/api/search?query=noisy&mode=text");

        Assert.Contains(semantic.EnumerateArray(),
            result => result.GetProperty("id").GetGuid() == pageId);
        Assert.Empty(literal.EnumerateArray());
    }

    /// <summary>What EmbeddingWorker's sweep would do, driven on demand so the test does
    /// not wait out an interval.</summary>
    private async Task EmbedEverythingAsync()
    {
        using var scope = factory.Services.CreateScope();
        var embeddings = scope.ServiceProvider.GetRequiredService<EmbeddingService>();
        foreach (var node in await embeddings.StaleNodesAsync(100))
            await embeddings.EmbedNodeAsync(node.Id);
    }
}
