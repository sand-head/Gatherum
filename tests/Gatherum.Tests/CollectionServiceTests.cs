using Gatherum.Core;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

/// <summary>The two documents meeting: a catalog somebody wrote, a tally per person,
/// and the grid that is nothing but the tallies a reader may enumerate.</summary>
[Collection("postgres")]
public class CollectionServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private CollectionService collections = null!;
    private FileService files = null!;
    private AccessService access = null!;
    private Guid alice;
    private Guid bob;

    private const string Roster = """
        # Override sprites

        :::collection Override sprites
        - Sonic
          - Base
          - Gold
        - Tails
        - Storm Scout
        :::
        """;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        collections = harness.Collections;
        files = harness.Files;
        access = harness.Access;
        alice = await harness.AddUserAsync("alice");
        bob = await harness.AddUserAsync("bob");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    private async Task<Node> CatalogAsync(AccessMode mode = AccessMode.Authenticated)
    {
        var page = await files.CreateTextNodeAsync(alice, null, "Override sprites", Roster);
        await access.SetAccessAsync(alice, page.Id, mode);
        return page;
    }

    private static string KeyOf(CollectionView view, string item, string? variant = null)
    {
        var row = view.Rows.Single(r => r.Text == item);
        return variant is null ? row.Key : row.Variants.Single(v => v.Text == variant).Key;
    }

    [Fact]
    public async Task A_catalog_reads_as_its_rows_before_anybody_has_ticked_anything()
    {
        var page = await CatalogAsync();

        var view = await collections.GetAsync(alice, page.Id);

        Assert.Equal("Override sprites", view.List);
        Assert.Equal(["Sonic", "Tails", "Storm Scout"], view.Rows.Select(r => r.Text));
        Assert.Equal(["Base", "Gold"], view.Rows[0].Variants.Select(v => v.Text));
        Assert.Empty(view.Columns);
        // Four collectibles across three lines: two of Sonic's variants, and the two
        // items that have none.
        Assert.Equal(4, view.Collectibles);
    }

    [Fact]
    public async Task A_first_tick_writes_the_tally_that_records_it()
    {
        var page = await CatalogAsync();

        var view = await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Tails"),
            collected: true);

        var mine = Assert.Single(view.Columns);
        Assert.True(mine.IsViewer);
        Assert.Equal(alice, mine.OwnerId);
        Assert.Equal(1, mine.Count);
        Assert.Equal(view.TallyId, (Guid?)mine.TallyId);

        var tally = await harness.ReloadAsync(alice, mine.TallyId);
        Assert.Equal("Override sprites", tally.Title);
        var folder = await harness.ReloadAsync(alice, tally.ParentId!.Value);
        Assert.Equal(CollectionService.TallyFolder, folder.Title);
    }

    /// <summary>A tally is a file under its owner's root, not a row in a table: it is
    /// carried by the backup people are told to take, and readable when nothing is
    /// running.</summary>
    [Fact]
    public async Task A_tally_is_a_page_that_says_what_it_is()
    {
        var page = await CatalogAsync();
        var view = await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Tails"),
            collected: true);

        var body = await files.GetTextAsync(alice, view.TallyId!.Value);
        var block = Assert.Single(CollectionSyntax.Read(body));

        Assert.False(block.Declares);
        Assert.Equal(page.Id, block.Tracks!.NodeId);
        Assert.Equal(["Sonic", "Tails", "Storm Scout"], block.Items.Select(i => i.Text));
        Assert.Equal([false, true, false], block.Items.Select(i => i.Checked));
    }

    [Fact]
    public async Task Everybody_gets_their_own_column_and_writes_only_their_own()
    {
        var page = await CatalogAsync();
        var rows = await Empty(page);
        await collections.SetAsync(alice, page.Id, KeyOf(rows, "Tails"), collected: true);
        await collections.SetAsync(bob, page.Id, KeyOf(rows, "Storm Scout"), collected: true);
        await collections.SetAsync(bob, page.Id, KeyOf(rows, "Sonic", "Gold"), collected: true);

        var view = await collections.GetAsync(bob, page.Id);

        Assert.Equal(2, view.Columns.Count);
        // The reader's own column leads, whoever has more.
        Assert.True(view.Columns[0].IsViewer);
        Assert.Equal(bob, view.Columns[0].OwnerId);
        Assert.Equal(2, view.Columns[0].Count);
        Assert.Equal(1, view.Columns[1].Count);
        Assert.NotEqual(view.Columns[0].TallyId, view.Columns[1].TallyId);
    }

    [Fact]
    public async Task Unticking_takes_it_back_off()
    {
        var page = await CatalogAsync();
        var key = KeyOf(await Empty(page), "Tails");
        await collections.SetAsync(alice, page.Id, key, collected: true);

        var view = await collections.SetAsync(alice, page.Id, key, collected: false);

        Assert.Equal(0, Assert.Single(view.Columns).Count);
    }

    /// <summary>Ticking a parent row is deliberately not a control: "give me all three"
    /// is a different statement from the three ticks it would stand in for, and the one
    /// thing this must not do is guess what somebody has.</summary>
    [Fact]
    public async Task Only_a_leaf_can_be_ticked()
    {
        var page = await CatalogAsync();
        var sonic = (await Empty(page)).Rows.Single(r => r.Text == "Sonic").Key;

        await Assert.ThrowsAsync<NotFoundException>(
            () => collections.SetAsync(alice, page.Id, sonic, collected: true));
    }

    [Fact]
    public async Task A_note_after_an_item_survives_the_next_tick()
    {
        var page = await CatalogAsync();
        var rows = await Empty(page);
        var view = await collections.SetAsync(alice, page.Id, KeyOf(rows, "Tails"), collected: true);
        var body = await files.GetTextAsync(alice, view.TallyId!.Value);
        await files.SaveTextAsync(alice, view.TallyId.Value,
            body.Replace("- [x] Tails", "- [x] Tails — traded for it"));

        await collections.SetAsync(alice, page.Id, KeyOf(rows, "Storm Scout"), collected: true);

        var again = CollectionSyntax.Read(
            await files.GetTextAsync(alice, view.TallyId.Value))[0];
        Assert.Equal("traded for it", again.Items.Single(i => i.Text == "Tails").Note);
    }

    /// <summary>The rule the grid runs on: whoever may read the list sees everyone's
    /// ticks against it. Ticking is joining in, and asking each participant to publish a
    /// second page before their column counted would be withholding a permission nobody
    /// meant to withhold.</summary>
    [Fact]
    public async Task Everyone_who_can_read_the_list_sees_everyone_s_ticks()
    {
        var page = await CatalogAsync();
        var mine = await collections.SetAsync(alice, page.Id,
            KeyOf(await Empty(page), "Tails"), collected: true);

        var hers = Assert.Single((await collections.GetAsync(bob, page.Id)).Columns);
        Assert.Equal(alice, hers.OwnerId);
        Assert.False(hers.IsViewer);
        Assert.Equal(1, hers.Count);
        // And nothing was published to do it: the page behind that column is still hers.
        var tally = await harness.ReloadAsync(alice, mine.TallyId!.Value);
        Assert.Equal(AccessMode.Private, tally.Access);
    }

    /// <summary>What a tally's own access still governs: the page. A column in the grid
    /// is not a license to open the file behind it, nor to find it in a tree or a
    /// search.</summary>
    [Fact]
    public async Task A_column_in_the_grid_does_not_open_the_page_behind_it()
    {
        var page = await CatalogAsync();
        var mine = await collections.SetAsync(alice, page.Id,
            KeyOf(await Empty(page), "Tails"), collected: true);

        Assert.Single((await collections.GetAsync(bob, page.Id)).Columns);

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.Nodes.GetVisibleAsync(bob, mine.TallyId!.Value));
        Assert.DoesNotContain(await harness.Nodes.GetTreeAsync(bob),
            n => n.Id == mine.TallyId!.Value);
    }

    /// <summary>The gate is the catalog and nothing else, so a list nobody may read has
    /// no grid to leak — not even the columns on it.</summary>
    [Fact]
    public async Task A_list_the_reader_cannot_see_has_no_columns_to_show()
    {
        var page = await CatalogAsync();
        await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Tails"),
            collected: true);
        await access.SetAccessAsync(alice, page.Id, AccessMode.Private);

        await Assert.ThrowsAsync<NotFoundException>(() => collections.GetAsync(bob, page.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => collections.GetAsync(null, page.Id));
    }

    /// <summary>An orphan is only actionable by whoever owns the file it is in, and the
    /// list's readers were shown ticks rather than the state of somebody's page.</summary>
    [Fact]
    public async Task Orphaned_ticks_are_reported_to_their_owner_and_nobody_else()
    {
        var page = await CatalogAsync();
        await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Tails"),
            collected: true);
        await files.SaveTextAsync(alice, page.Id, Roster.Replace("- Tails", "- Tails the Fox"));

        Assert.NotEmpty(Assert.Single((await collections.GetAsync(alice, page.Id)).Columns)
            .Orphans);
        Assert.Empty(Assert.Single((await collections.GetAsync(bob, page.Id)).Columns).Orphans);
    }

    [Fact]
    public async Task Signed_out_reads_a_public_list_and_writes_nothing()
    {
        var page = await CatalogAsync(AccessMode.Public);
        await collections.SetAsync(alice, page.Id,
            KeyOf(await Empty(page), "Tails"), collected: true);

        var view = await collections.GetAsync(null, page.Id);

        Assert.False(view.CanTick);
        Assert.Null(view.TallyId);
        Assert.Equal(1, Assert.Single(view.Columns).Count);
        Assert.False(view.Columns[0].IsViewer);
    }

    /// <summary>Ticks are made against a text item, then the item gains a page. The
    /// promotion has to be lossless or nobody will ever do it.</summary>
    [Fact]
    public async Task Ticks_keep_counting_when_an_item_gains_a_page()
    {
        var page = await CatalogAsync();
        await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Tails"),
            collected: true);
        var sprite = await files.CreateTextNodeAsync(alice, null, "Tails", "A fox.");

        await files.SaveTextAsync(alice, page.Id,
            Roster.Replace("- Tails", $"- [Tails](node://{sprite.Id})"));

        var view = await collections.GetAsync(alice, page.Id);
        Assert.Equal(sprite.Id, view.Rows.Single(r => r.Text == "Tails").NodeId);
        Assert.Equal(1, Assert.Single(view.Columns).Count);
    }

    /// <summary>Alice cannot rewrite Bob's tally to follow her rename — it is his file —
    /// so the ticks stop matching. Saying so is the requirement; silence is what would
    /// be unacceptable.</summary>
    [Fact]
    public async Task A_rename_orphans_the_ticks_it_stranded_and_says_so()
    {
        var page = await CatalogAsync();
        await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Sonic", "Gold"),
            collected: true);
        await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Tails"),
            collected: true);

        await files.SaveTextAsync(alice, page.Id,
            Roster.Replace("- Tails", "- Tails the Fox").Replace("  - Gold", "  - Cheat Master"));

        var mine = Assert.Single((await collections.GetAsync(alice, page.Id)).Columns);
        Assert.Equal(0, mine.Count);
        Assert.Equal(["Sonic — Gold", "Tails"], mine.Orphans.Select(o => o.Text).Order());
    }

    /// <summary>An orphan is kept in the file, so a catalog that changes its mind back
    /// finds the tick still there.</summary>
    [Fact]
    public async Task An_orphaned_tick_comes_back_when_its_item_does()
    {
        var page = await CatalogAsync();
        await collections.SetAsync(alice, page.Id, KeyOf(await Empty(page), "Tails"),
            collected: true);
        await files.SaveTextAsync(alice, page.Id, Roster.Replace("- Tails", "- Tails the Fox"));
        // Another tick, so the tally is rewritten while the orphan is stranded.
        await collections.SetAsync(alice, page.Id,
            KeyOf(await collections.GetAsync(alice, page.Id), "Storm Scout"), collected: true);

        await files.SaveTextAsync(alice, page.Id, Roster);

        var mine = Assert.Single((await collections.GetAsync(alice, page.Id)).Columns);
        Assert.Empty(mine.Orphans);
        Assert.Equal(2, mine.Count);
    }

    [Fact]
    public async Task A_page_that_merely_mentions_the_catalog_is_not_somebody_s_column()
    {
        var page = await CatalogAsync();
        var notes = await files.CreateTextNodeAsync(bob, null, "Notes", $"""
            I am reading [the roster](node://{page.Id}), and I have:

            - [x] Tails
            """);
        await access.SetAccessAsync(bob, notes.Id, AccessMode.Authenticated);

        Assert.Empty((await collections.GetAsync(alice, page.Id)).Columns);
    }

    [Fact]
    public async Task Two_lists_on_one_page_are_tallied_separately()
    {
        var page = await files.CreateTextNodeAsync(alice, null, "Season 4", """
            :::collection Sprites
            - Sonic
            - Tails
            :::

            :::collection Emotes
            - Floss
            :::
            """);

        var sprites = await collections.GetAsync(alice, page.Id, "Sprites");
        var emotes = await collections.GetAsync(alice, page.Id, "Emotes");
        Assert.Equal(["Sonic", "Tails"], sprites.Rows.Select(r => r.Text));
        Assert.Equal(["Floss"], emotes.Rows.Select(r => r.Text));

        await collections.SetAsync(alice, page.Id, KeyOf(sprites, "Sonic"), collected: true,
            list: "Sprites");

        Assert.Equal(1, Assert.Single(
            (await collections.GetAsync(alice, page.Id, "Sprites")).Columns).Count);
        Assert.Empty((await collections.GetAsync(alice, page.Id, "Emotes")).Columns);
    }

    /// <summary>A tally page is a place to read the grid from too — it aggregates the
    /// catalog it tracks rather than only itself.</summary>
    [Fact]
    public async Task A_tally_page_shows_the_same_grid_the_catalog_does()
    {
        var page = await CatalogAsync();
        var mine = await collections.SetAsync(alice, page.Id,
            KeyOf(await Empty(page), "Tails"), collected: true);

        var view = await collections.GetAsync(alice, mine.TallyId!.Value);

        Assert.Equal(page.Id, view.CatalogId);
        Assert.Equal(["Sonic", "Tails", "Storm Scout"], view.Rows.Select(r => r.Text));
        Assert.Equal(1, Assert.Single(view.Columns).Count);
    }

    /// <summary>A title is a search and an id is permission, so a tally spelled with a
    /// wiki link follows a catalog it can enumerate.</summary>
    [Fact]
    public async Task A_tally_may_name_its_catalog_by_title()
    {
        var page = await CatalogAsync();
        await files.CreateTextNodeAsync(bob, null, "My sprites", """
            :::collection [[Override sprites]]
            - [x] Tails
            :::
            """);

        var column = Assert.Single((await collections.GetAsync(bob, page.Id)).Columns);
        Assert.Equal(bob, column.OwnerId);
        Assert.Equal(1, column.Count);
    }

    [Fact]
    public async Task A_page_with_no_collection_on_it_has_none()
    {
        var page = await files.CreateTextNodeAsync(alice, null, "Plain", "Nothing here.");

        await Assert.ThrowsAsync<NotFoundException>(() => collections.GetAsync(alice, page.Id));
    }

    [Fact]
    public async Task A_catalog_nobody_shared_stays_invisible()
    {
        var page = await CatalogAsync(AccessMode.Private);

        await Assert.ThrowsAsync<NotFoundException>(() => collections.GetAsync(bob, page.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => collections.GetAsync(null, page.Id));
    }

    /// <summary>The mechanism knows nothing about collectibles: "who has which sprite"
    /// and "who can make which night" are the same grid asked of different nouns, and the
    /// fence's word is carried through so the reading view can say which.</summary>
    [Fact]
    public async Task The_same_grid_answers_a_different_question()
    {
        var page = await files.CreateTextNodeAsync(alice, null, "Game nights", """
            :::availability Game nights
            - Fri 3 Oct
            - Fri 10 Oct
            - Fri 17 Oct
            :::
            """);
        await access.SetAccessAsync(alice, page.Id, AccessMode.Authenticated);

        var view = await collections.GetAsync(alice, page.Id);
        Assert.Equal("availability", view.Kind);
        Assert.Equal(3, view.Collectibles);

        await collections.SetAsync(alice, page.Id, KeyOf(view, "Fri 3 Oct"), collected: true);
        await collections.SetAsync(bob, page.Id, KeyOf(view, "Fri 3 Oct"), collected: true);
        await collections.SetAsync(bob, page.Id, KeyOf(view, "Fri 17 Oct"), collected: true);

        var everyone = await collections.GetAsync(bob, page.Id);
        Assert.Equal(2, everyone.Columns.Count);
        Assert.Equal([2, 1], everyone.Columns.Select(c => c.Count));
        // And the tally it wrote opens with the catalog's word, not the default one.
        var tally = await files.GetTextAsync(bob, everyone.TallyId!.Value);
        Assert.StartsWith(":::availability ", tally, StringComparison.Ordinal);
    }

    /// <summary>A poll is the same grid asked with one answer each, so picking is moving
    /// rather than adding. Enforced on the write, because the file is what everybody else
    /// reads and a tally claiming two answers would be wrong wherever it was opened.</summary>
    [Fact]
    public async Task A_poll_takes_back_the_last_answer_when_a_new_one_is_given()
    {
        var page = await files.CreateTextNodeAsync(alice, null, "Where for dinner?", """
            :::poll Dinner
            - Thai
            - Pizza
            - Sushi
            :::
            """);
        await access.SetAccessAsync(alice, page.Id, AccessMode.Authenticated);
        var rows = await collections.GetAsync(alice, page.Id);

        await collections.SetAsync(alice, page.Id, KeyOf(rows, "Thai"), collected: true);
        var moved = await collections.SetAsync(alice, page.Id, KeyOf(rows, "Sushi"),
            collected: true);

        var mine = Assert.Single(moved.Columns);
        Assert.Equal(1, mine.Count);
        Assert.Equal([KeyOf(rows, "Sushi")], mine.Held);
        // And the file says one answer, not two.
        var tally = await files.GetTextAsync(alice, moved.TallyId!.Value);
        Assert.Single(CollectionSyntax.Read(tally)[0].Items, i => i.Checked);
    }

    /// <summary>Taking an answer back is still taking it back: a poll is one answer at
    /// most, not one answer compulsorily.</summary>
    [Fact]
    public async Task A_poll_answer_can_be_withdrawn()
    {
        var page = await files.CreateTextNodeAsync(alice, null, "Where for dinner?", """
            :::poll Dinner
            - Thai
            - Pizza
            :::
            """);
        var thai = KeyOf(await collections.GetAsync(alice, page.Id), "Thai");
        await collections.SetAsync(alice, page.Id, thai, collected: true);

        var view = await collections.SetAsync(alice, page.Id, thai, collected: false);

        Assert.Equal(0, Assert.Single(view.Columns).Count);
    }

    /// <summary>Every other word keeps every answer — the constraint is the poll's alone,
    /// and nothing else changed underneath it.</summary>
    [Fact]
    public async Task Only_a_poll_takes_the_last_answer_back()
    {
        var page = await CatalogAsync();
        var rows = await Empty(page);

        await collections.SetAsync(alice, page.Id, KeyOf(rows, "Tails"), collected: true);
        var both = await collections.SetAsync(alice, page.Id, KeyOf(rows, "Storm Scout"),
            collected: true);

        Assert.Equal(2, Assert.Single(both.Columns).Count);
    }

    private Task<CollectionView> Empty(Node page) => collections.GetAsync(alice, page.Id);
}
