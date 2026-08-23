using Gatherum.Core.Services;

namespace Gatherum.Tests;

/// <summary>What the vector half is for, and what it must never do. FakeEmbedder stands
/// in for the model so that "finds a page written in other words" is an assertion rather
/// than a hope — see its remarks for how it earns that.</summary>
[Collection("postgres")]
public class SemanticSearchTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        jess = await harness.AddUserAsync("jess");
        sam = await harness.AddUserAsync("sam");
        // One subject, spelled two ways that share no word with each other.
        harness.Embedder.Means("cooling", "thermals", "overheating", "noisy");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task Finds_a_page_that_answers_the_question_in_other_words()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Rack notes",
            "The overheating started after the third drive went in.");
        await harness.EmbedStaleAsync();

        var hybrid = await harness.Search.SearchAsync(jess, "noisy");
        var literal = await harness.Search.SearchAsync(jess, "noisy", mode: SearchMode.Text);

        Assert.Equal([page.Id], hybrid.Select(result => result.Id));
        Assert.Empty(literal);
    }

    [Fact]
    public async Task The_snippet_of_a_meaning_match_is_the_passage_that_matched()
    {
        await harness.Files.CreateTextNodeAsync(jess, null, "Rack notes",
            "Unrelated opening paragraph about invoices.\n\n" +
            "The overheating started after the third drive went in.");
        await harness.EmbedStaleAsync();

        var hit = Assert.Single(await harness.Search.SearchAsync(jess, "noisy"));

        Assert.Contains("overheating", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Words_nobody_has_written_still_find_nothing()
    {
        await harness.Files.CreateTextNodeAsync(jess, null, "Rack notes",
            "The overheating started after the third drive went in.");
        await harness.EmbedStaleAsync();

        Assert.Empty(await harness.Search.SearchAsync(jess, "bicycle maintenance schedule"));
    }

    [Fact]
    public async Task A_private_page_is_invisible_to_the_vector_half_too()
    {
        var page = await harness.Files.CreateTextNodeAsync(sam, null, "Sam's rack",
            "The overheating started after the third drive went in.");
        await harness.EmbedStaleAsync();

        Assert.Empty(await harness.Search.SearchAsync(jess, "noisy"));
        Assert.Equal([page.Id],
            (await harness.Search.SearchAsync(sam, "noisy")).Select(result => result.Id));
    }

    [Fact]
    public async Task A_search_survives_a_model_that_is_down()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Rack notes",
            "The overheating started after the third drive went in.");
        await harness.EmbedStaleAsync();
        harness.Embedder.Throws = new HttpRequestException("connection refused");

        var results = await harness.Search.SearchAsync(jess, "overheating");

        Assert.Equal([page.Id], results.Select(result => result.Id));
    }

    [Fact]
    public async Task A_search_survives_a_model_that_is_too_slow_to_wait_for()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Rack notes",
            "The overheating started after the third drive went in.");
        await harness.EmbedStaleAsync();
        harness.Embedder.Delay = TimeSpan.FromSeconds(30);

        var results = await harness.Search.SearchAsync(jess, "overheating");

        Assert.Equal([page.Id], results.Select(result => result.Id));
    }

    [Fact]
    public async Task Asking_for_meaning_alone_when_there_is_no_model_still_searches()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Rack notes",
            "The overheating started after the third drive went in.");
        await harness.EmbedStaleAsync();
        harness.Embedder.Throws = new HttpRequestException("connection refused");

        var results = await harness.Search.SearchAsync(jess, "overheating",
            mode: SearchMode.Semantic);

        Assert.Equal([page.Id], results.Select(result => result.Id));
    }

    [Fact]
    public async Task An_edit_re_embeds_only_the_passage_that_changed()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Long page",
            Paragraph("alpha") + "\n\n" + Paragraph("beta") + "\n\n" + Paragraph("gamma"));
        await harness.EmbedStaleAsync();
        var first = harness.Embedder.Embedded.Count;
        Assert.True(first >= 3, $"expected several passages, embedded {first}");

        harness.Embedder.Embedded.Clear();
        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.Files.SaveTextAsync(jess, page.Id,
            Paragraph("alpha") + "\n\n" + Paragraph("beta") + "\n\n" + Paragraph("delta"));
        await harness.EmbedStaleAsync();

        // The passage that changed, and the one that carries its tail as overlap.
        Assert.InRange(harness.Embedder.Embedded.Count, 1, 2);
    }

    [Fact]
    public async Task Renaming_a_category_re_embeds_the_pages_filed_under_it()
    {
        var page = await harness.Files.CreateTextNodeAsync(jess, null, "Chapter 3",
            "Nothing in this body mentions the subject at all.");
        await harness.Categories.AddAsync(jess, page.Id, "Fiction");
        await harness.EmbedStaleAsync();
        harness.Embedder.Embedded.Clear();

        await harness.Categories.RenameAsync("fiction", "Worldbuilding");
        await harness.EmbedStaleAsync();

        Assert.NotEmpty(harness.Embedder.Embedded);
        Assert.Contains(harness.Embedder.Embedded,
            text => text.Contains("Worldbuilding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Similar_reaches_a_page_sharing_no_category_and_no_link()
    {
        var subject = await harness.Files.CreateTextNodeAsync(jess, null, "Rack notes",
            "The overheating started after the third drive went in.");
        var kin = await harness.Files.CreateTextNodeAsync(jess, null, "Closet log",
            "Noisy all afternoon; propped the door open.");
        await harness.Files.CreateTextNodeAsync(jess, null, "Invoices",
            "Quarterly billing reconciliation for the accountant.");
        await harness.EmbedStaleAsync();

        var similar = await harness.Nodes.GetSimilarAsync(jess, subject.Id);

        Assert.Equal([kin.Id], similar.Select(node => node.Id));
    }

    /// <summary>A paragraph long enough to be a passage of its own at the harness's
    /// chunk size, distinguished by one word.</summary>
    private static string Paragraph(string marker) =>
        string.Join(' ', Enumerable.Repeat($"{marker} filler words to fill the passage.", 12));
}
