using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Services;
using Gatherum.Web.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
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
    private string connectionString = "";
    private readonly FakeEmbedder embedder = new();

    public async Task InitializeAsync()
    {
        connectionString = await postgres.CreateDatabaseAsync();
        storageRoot = Path.Combine(Path.GetTempPath(), $"gatherum-it-{Guid.NewGuid():N}");
        embedder.Means("cooling", "thermals", "overheating", "noisy");
        factory = CreateFactory();
        await SeedAsync();
    }

    /// <summary>The app under test. Rate-limit windows are keyed by client address and
    /// shared across every client of one instance, so a test that means to exhaust a
    /// budget gets an instance of its own rather than spending everybody else's.</summary>
    private WebApplicationFactory<Program> CreateFactory(
        params (string Key, string Value)[] overrides) => CreateFactory(null, overrides);

    private WebApplicationFactory<Program> CreateFactory(string? environment,
        params (string Key, string Value)[] overrides)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (environment is not null)
                builder.UseSetting(WebHostDefaults.EnvironmentKey, environment);
            builder.UseSetting("Gatherum:Database:ConnectionString", connectionString);
            builder.UseSetting("Gatherum:Storage:Root", storageRoot);
            // Generous here so the other anonymous tests are never metered; the budget
            // test builds its own app with limits it can exhaust deliberately.
            builder.UseSetting("Gatherum:Sharing:AnonymousReadsPerMinute", "1000");
            builder.UseSetting("Gatherum:Sharing:AnonymousSearchesPerMinute", "1000");
            foreach (var (key, value) in overrides)
                builder.UseSetting(key, value);
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
    }

    private async Task SeedAsync() => client = await AuthorClientAsync(factory);

    /// <summary>A signed-in client for an app: the user and their API key, since every
    /// instance shares one database but each needs its own key issued through it.</summary>
    private static async Task<HttpClient> AuthorClientAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        var user = await users.GetOrCreateAsync("it-user", "it@example.org", "Integration", "tester");
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyService>();
        var created = await keys.CreateAsync(user.Id, "integration");

        var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", created.PlaintextToken);
        return http;
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
    public async Task Api_writes_and_mcp_reject_missing_keys()
    {
        using var anonymous = factory.CreateClient();

        // Reads are reachable without a key now that a node can be public — they simply
        // show the caller nothing but public nodes, which is none of these.
        var tree = await anonymous.GetAsync("/api/nodes/tree");
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        Assert.Empty((await tree.Content.ReadFromJsonAsync<List<TreeNodeDto>>())!);

        // Everything that writes still refuses, whatever any node's access says.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(
            "/api/pages", new { title = "smuggled" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/keys")).StatusCode);

        var mcp = await anonymous.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "ping" });
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
    }

    [Fact]
    public async Task A_published_page_tells_a_stranger_which_of_its_links_they_may_follow()
    {
        var secret = (await (await client.PostAsJsonAsync("/api/pages",
            new { title = "Rack inventory", markdown = "serials and passwords" }))
            .Content.ReadFromJsonAsync<NodeDto>())!;
        var published = (await (await client.PostAsJsonAsync("/api/pages",
            new
            {
                title = "The homelab",
                markdown = $"What is in it: [@Rack inventory](node://{secret.Id}).",
            })).Content.ReadFromJsonAsync<NodeDto>())!;
        await client.PostAsJsonAsync($"/api/nodes/{published.Id}/access", new { access = "Public" });

        using var anonymous = factory.CreateClient();

        // The page is theirs to read, mention and all — publishing a page publishes what
        // it says, not what it points at.
        var page = await anonymous.GetStringAsync($"/api/files/{published.Id}/content");
        Assert.Contains($"node://{secret.Id}", page);

        // And this is what the reader dresses that mention with: the link's own node,
        // unanswered for.
        var reachable = await anonymous.PostAsJsonAsync("/api/nodes/reachable",
            new { ids = new[] { secret.Id, published.Id } });
        Assert.Equal([published.Id],
            await reachable.Content.ReadFromJsonAsync<List<Guid>>());
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/nodes/{secret.Id}")).StatusCode);

        // Its author is told the truth for their own session: both.
        var mine = await client.PostAsJsonAsync("/api/nodes/reachable",
            new { ids = new[] { secret.Id, published.Id } });
        Assert.Equal(2, (await mine.Content.ReadFromJsonAsync<List<Guid>>())!.Count);
    }

    [Fact]
    public async Task The_internet_gets_a_budget_and_a_signed_in_user_does_not()
    {
        using var metered = CreateFactory(
            // Small enough to exhaust deliberately, with no queue, so an over-budget
            // request is refused rather than made to wait.
            ("Gatherum:Sharing:AnonymousReadsPerMinute", "3"),
            ("Gatherum:Sharing:AnonymousSearchesPerMinute", "2"),
            ("Gatherum:Sharing:AnonymousQueueDepth", "0"));
        using var author = await AuthorClientAsync(metered);

        var page = (await (await author.PostAsJsonAsync("/api/pages",
            new { title = "Published", markdown = "the closet gets hot" }))
            .Content.ReadFromJsonAsync<NodeDto>())!;
        await author.PostAsJsonAsync($"/api/nodes/{page.Id}/access", new { access = "Public" });

        using var anonymous = metered.CreateClient();

        // Three reads a minute, then refused — with a Retry-After a well-behaved client
        // can act on.
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/nodes/{page.Id}")).StatusCode);

        var refused = await anonymous.GetAsync($"/api/nodes/{page.Id}");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);

        // Search has its own, tighter budget: its semantic half runs a model on the
        // request path, so it is metered apart from plain reads.
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/search?query=closet")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await anonymous.GetAsync("/api/search?query=closet")).StatusCode);

        // The two people who authenticated to get here are never metered.
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK, (await author.GetAsync($"/api/nodes/{page.Id}")).StatusCode);
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK, (await author.GetAsync("/api/search?query=closet")).StatusCode);
    }

    [Fact]
    public async Task A_restart_does_not_sign_everybody_out()
    {
        // Sign-in cookies are protected by Data Protection keys. Left to itself ASP.NET
        // keeps them under the runtime user's home directory, which is inside the image —
        // and unwritable when the container runs as a uid the image has no entry for, at
        // which point the keys die with the process. Keeping them in the database is what
        // makes a cookie issued before a restart still valid after one, so this protects
        // a payload in one instance and unprotects it in another over the same database.
        const string cookie = "a session that should outlive the container";

        var issued = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("cookies").Protect(cookie);

        using var afterRestart = CreateFactory();
        // Touch it so the host is built and the keys are read back rather than made.
        _ = afterRestart.CreateClient();

        var recovered = afterRestart.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("cookies").Unprotect(issued);

        Assert.Equal(cookie, recovered);

        // And they are in the database, not on a disk somebody has to give the container
        // permission to write.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatherumDbContext>();
        Assert.NotEmpty(await db.DataProtectionKeys.ToListAsync());
    }

    [Fact]
    public async Task The_error_pages_answer_a_signed_out_visitor_instead_of_looping()
    {
        // Every page needs a signed-in user, and these two are where an unauthenticated
        // request lands when something goes wrong. If they demand a login too, the login
        // failure that brought you there sends you back, and the pair redirect at each
        // other forever with the return URL re-encoding each lap — an error page that
        // hides the error, which is the worst possible thing for one to do.
        using var anonymous = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in new[] { "/Error", "/not-found" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.False(IsRedirectToLogin(response), $"{path} redirected to sign in.");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // A path matching no route is a different question and deliberately left alone:
        // the fallback policy sends an anonymous stranger to sign in before any 404 is
        // reached. That is not the loop — nothing re-executes — and changing it would be
        // a decision about what an unauthenticated visitor is allowed to learn exists.
        Assert.True(IsRedirectToLogin(await anonymous.GetAsync("/no-such-page")));
    }

    private static bool IsRedirectToLogin(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found
            && response.Headers.Location?.OriginalString.Contains("/auth/login") == true;

    [Fact]
    public async Task A_stranger_can_read_a_published_page_in_a_browser()
    {
        // The half that was missing: publishing worked for curl against /api and not for
        // a person following a link, which is close to the opposite of the point.
        var page = (await (await client.PostAsJsonAsync("/api/pages",
            new { title = "Published", markdown = "the closet gets hot" }))
            .Content.ReadFromJsonAsync<NodeDto>())!;
        var draft = (await (await client.PostAsJsonAsync("/api/pages",
            new { title = "Draft", markdown = "not yet" })).Content.ReadFromJsonAsync<NodeDto>())!;
        await client.PostAsJsonAsync($"/api/nodes/{page.Id}/access", new { access = "Public" });

        using var anonymous = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var read = await anonymous.GetAsync($"/nodes/{page.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var html = await read.Content.ReadAsStringAsync();
        Assert.Contains("Published", html);
        // Signed out means read-only, whatever the node says.
        Assert.DoesNotContain($"/nodes/{page.Id}?edit", html);
        Assert.Contains("/auth/login", html);

        // The front door lists it, and lists nothing else.
        var home = await anonymous.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var index = await home.Content.ReadAsStringAsync();
        Assert.Contains($"/nodes/{page.Id}", index);
        Assert.DoesNotContain($"/nodes/{draft.Id}", index);

        // And the unpublished one stays shut.
        var refused = await anonymous.GetAsync($"/nodes/{draft.Id}");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("doesn\u0027t exist", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unlisted_page_opens_from_its_link_and_appears_in_no_index()
    {
        var unlisted = (await (await client.PostAsJsonAsync("/api/pages",
            new { title = "Half-written", markdown = "not ready to announce" }))
            .Content.ReadFromJsonAsync<NodeDto>())!;
        await client.PostAsJsonAsync($"/api/nodes/{unlisted.Id}/access", new { access = "Unlisted" });

        using var anonymous = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The link is the permission.
        var read = await anonymous.GetAsync($"/nodes/{unlisted.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var html = await read.Content.ReadAsStringAsync();
        Assert.Contains("Half-written", html);
        // A crawler arriving by a leaked referrer must not publish what a link shared.
        Assert.Contains("noindex", html);

        // And nothing hands that link out.
        Assert.DoesNotContain($"/nodes/{unlisted.Id}",
            await (await anonymous.GetAsync("/")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(unlisted.Id.ToString(),
            await anonymous.GetStringAsync("/api/nodes/tree"));
        Assert.Empty((await anonymous.GetFromJsonAsync<List<SearchResultDto>>(
            "/api/search?query=announce"))!);
    }

    [Fact]
    public void Without_an_identity_provider_the_app_refuses_to_start_outside_development()
    {
        // The development auto-login signs in whoever asks, without authenticating them.
        // Deployed, that is not a warning in a log — it is an open door, and the app is
        // not allowed to be one.
        using var misconfigured = CreateFactory("Production");

        var refused = Assert.Throws<InvalidOperationException>(
            () => misconfigured.CreateClient());

        Assert.Contains("No identity provider is configured", refused.Message);
        Assert.Contains("signs in anyone who asks", refused.Message);
    }

    [Fact]
    public async Task Behind_a_proxy_the_budget_follows_the_forwarded_client_address()
    {
        // The rate limiter partitions on RemoteIpAddress, and behind a TLS-terminating
        // reverse proxy that is the proxy for everybody — one bucket for the whole
        // internet — unless X-Forwarded-For is honoured. The container turns that on with
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED, which is a setting in the Dockerfile rather
        // than a line of code here, so this pins the behaviour the limiter depends on.
        var before = Environment.GetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED");
        Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", "true");
        try
        {
            using var proxied = CreateFactory(
                ("Gatherum:Sharing:AnonymousReadsPerMinute", "2"),
                ("Gatherum:Sharing:AnonymousQueueDepth", "0"));
            using var anonymous = proxied.CreateClient();

            // Four requests against a budget of two, each from a different client as a
            // proxy would report it. Every one is served: they are four callers, not one.
            for (var i = 1; i <= 4; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nodes/tree");
                request.Headers.Add("X-Forwarded-For", $"203.0.113.{i}");
                Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(request)).StatusCode);
            }

            // And one caller is still one caller.
            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < 3; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/nodes/tree");
                request.Headers.Add("X-Forwarded-For", "198.51.100.9");
                codes.Add((await anonymous.SendAsync(request)).StatusCode);
            }
            Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_FORWARDEDHEADERS_ENABLED", before);
        }
    }

    [Fact]
    public async Task A_published_page_is_readable_from_the_internet_and_nothing_else_is()
    {
        var published = await client.PostAsJsonAsync("/api/pages",
            new { title = "Published", markdown = "the closet gets hot" });
        var page = (await published.Content.ReadFromJsonAsync<NodeDto>())!;
        var draft = (await (await client.PostAsJsonAsync("/api/pages",
            new { title = "Draft", markdown = "not yet" })).Content.ReadFromJsonAsync<NodeDto>())!;

        using var anonymous = factory.CreateClient();

        // Private by default: an unpublished page is not there at all.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/nodes/{page.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            $"/api/nodes/{page.Id}/access", new { access = "Public" })).StatusCode);

        // Public means the internet: no session, no key.
        var read = await anonymous.GetAsync($"/api/nodes/{page.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("Published", (await read.Content.ReadFromJsonAsync<NodeDto>())!.Title);

        // Its content and its searchability come with it.
        var content = await anonymous.GetAsync($"/api/files/{page.Id}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Contains("closet", await content.Content.ReadAsStringAsync());

        var hits = await anonymous.GetFromJsonAsync<List<SearchResultDto>>(
            "/api/search?query=closet");
        Assert.Equal([page.Id], hits!.Select(h => h.Id));

        // And nothing else does.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/nodes/{draft.Id}")).StatusCode);
        Assert.Equal([page.Id],
            (await anonymous.GetFromJsonAsync<List<TreeNodeDto>>("/api/nodes/tree"))!
                .Select(n => n.Id));

        // A reader from the internet is still only a reader.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(
            $"/api/nodes/{page.Id}/rename", new { title = "defaced" })).StatusCode);
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
