using Gatherum.Client;
using Gatherum.Core.Markdown;

namespace Gatherum.Tests;

/// <summary>The construct on its own, with no database under it: what a fence says, what
/// survives being written back out, and which two lines are the same row.</summary>
public class CollectionSyntaxTests
{
    private const string Catalogue = """
        Sprites arrive on Thursdays.

        :::collection Override sprites
        - Sonic
          - Base
          - Gold
        - [Klombo](node://0193aaaa-bbbb-cccc-dddd-eeeeeeeeeeee)
        - Storm Scout
          - Base
        :::

        More prose after it.
        """;

    [Fact]
    public void A_named_fence_declares_a_list()
    {
        var block = Assert.Single(CollectionSyntax.Read(Catalogue));

        Assert.True(block.Declares);
        Assert.Equal("Override sprites", block.Name);
        Assert.Equal(["Sonic", "Klombo", "Storm Scout"], block.Items.Select(i => i.Text));
    }

    [Fact]
    public void Variants_are_the_items_nested_under_one()
    {
        var items = CollectionSyntax.Read(Catalogue)[0].Items;

        Assert.Equal(["Base", "Gold"], items[0].Variants.Select(v => v.Text));
        Assert.Empty(items[1].Variants);
        Assert.Equal(["Base"], items[2].Variants.Select(v => v.Text));
    }

    /// <summary>The count every interface has to report. Twelve lines of eleven sprites
    /// at three variants each is not twelve collectibles, and a progress bar that says
    /// otherwise is fiction.</summary>
    [Fact]
    public void A_ragged_roster_counts_collectibles_rather_than_lines()
    {
        var items = CollectionSyntax.Read(Catalogue)[0].Items;

        Assert.Equal(4, items.Sum(i => i.Collectibles));
    }

    [Fact]
    public void An_item_that_links_a_page_carries_its_id_and_reads_as_its_title()
    {
        var klombo = CollectionSyntax.Read(Catalogue)[0].Items[1];

        Assert.Equal(Guid.Parse("0193aaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), klombo.NodeId);
        Assert.Equal("Klombo", klombo.Text);
    }

    [Fact]
    public void A_fence_naming_another_node_tracks_it_instead_of_declaring_one()
    {
        var byTitle = CollectionSyntax.Read("""
            :::collection [[Override sprites]]
            - [x] Sonic
            - [ ] Tails
            :::
            """)[0];

        Assert.False(byTitle.Declares);
        Assert.Equal("Override sprites", byTitle.Tracks!.Title);
        Assert.Null(byTitle.Tracks.NodeId);
        Assert.Equal([true, false], byTitle.Items.Select(i => i.Checked));
    }

    /// <summary>A title is a search and an id is permission, so the mention spelling is
    /// the one that can name an unlisted catalogue.</summary>
    [Fact]
    public void A_tally_can_name_its_catalogue_by_id()
    {
        var id = Guid.NewGuid();

        var block = CollectionSyntax.Read($$"""
            :::collection [Override sprites](node://{{id}})
            - [x] Sonic
            :::
            """)[0];

        Assert.Equal(id, block.Tracks!.NodeId);
        Assert.Equal("", block.Tracks.List);
    }

    [Fact]
    public void A_tally_can_say_which_list_on_that_node_it_follows()
    {
        var block = CollectionSyntax.Read("""
            :::collection [[Season 4]] Sprites
            - [x] Sonic
            :::
            """)[0];

        Assert.Equal("Season 4", block.Tracks!.Title);
        Assert.Equal("Sprites", block.Tracks.List);
        Assert.Equal("Sprites", block.Name);
    }

    [Fact]
    public void Prose_after_an_item_is_a_note_rather_than_part_of_it()
    {
        var items = CollectionSyntax.Read("""
            :::collection [[Sprites]]
            - [x] Sonic — Gold, Sprite Day 2
            - [x] Tails -- traded for it
            :::
            """)[0].Items;

        Assert.Equal("Sonic", items[0].Text);
        Assert.Equal("Gold, Sprite Day 2", items[0].Note);
        Assert.Equal("Tails", items[1].Text);
        Assert.Equal("traded for it", items[1].Note);
    }

    [Fact]
    public void Two_lists_on_one_page_are_two_lists()
    {
        var blocks = CollectionSyntax.Read("""
            :::collection Sprites
            - Sonic
            :::

            :::collection Emotes
            - Floss
            :::
            """);

        Assert.Equal(["Sprites", "Emotes"], blocks.Select(b => b.Name));
        Assert.Equal("Emotes", CollectionSyntax.Find(
            """
            :::collection Sprites
            - Sonic
            :::

            :::collection Emotes
            - Floss
            :::
            """, "Emotes")!.Name);
    }

    [Fact]
    public void An_unterminated_fence_is_not_a_collection()
    {
        Assert.Empty(CollectionSyntax.Read("""
            :::collection Sprites
            - Sonic
            """));
    }

    /// <summary>One grid, several questions. "Who has which sprite" and "who can make
    /// which night" differ in nothing but the noun, so they are one construct with a small
    /// vocabulary — the shape callouts already have.</summary>
    [Fact]
    public void Every_word_in_the_vocabulary_opens_the_same_construct()
    {
        foreach (var word in CollectionSyntax.Kinds)
        {
            var block = Assert.Single(CollectionSyntax.Read($"""
                :::{word} Game nights
                - Fri 3 Oct
                - Fri 10 Oct
                :::
                """));

            Assert.Equal(word, block.Word);
            Assert.True(block.Declares);
            Assert.Equal("Game nights", block.Name);
            Assert.Equal(["Fri 3 Oct", "Fri 10 Oct"], block.Items.Select(i => i.Text));
        }
    }

    /// <summary>The word is the source's, so it survives the trip — a list of nights does
    /// not come back a collection of them.</summary>
    [Fact]
    public void A_lists_own_word_is_written_back()
    {
        const string source = """
            :::availability [[Game nights]]
            - [x] Fri 3 Oct
            - [ ] Fri 10 Oct
            :::
            """;

        var block = CollectionSyntax.Read(source)[0];

        Assert.Equal(source.ReplaceLineEndings("\n"),
            CollectionSyntax.Write(block.Word, block.Argument, block.Items, ticked: true));
    }

    /// <summary>Two lists in two places, and no third: Core parses the words, the client
    /// says what each one calls things, and a word in one and not the other would render
    /// a grid that reads as the wrong question.</summary>
    [Fact]
    public void The_parser_and_the_reading_view_know_the_same_words()
    {
        Assert.Equal(CollectionSyntax.Kinds.Order(), ListVocabulary.All.Keys.Order());
        Assert.All(ListVocabulary.All.Values, words =>
        {
            Assert.NotEmpty(words.Rows);
            Assert.Contains("{0}", words.Total, StringComparison.Ordinal);
            Assert.Contains("{0}", words.Score, StringComparison.Ordinal);
            Assert.Contains("{1}", words.Score, StringComparison.Ordinal);
            Assert.NotEmpty(words.Invite);
            Assert.NotEmpty(words.Yes);
            Assert.NotEmpty(words.No);
        });
        // An unknown word still draws a grid rather than nothing.
        Assert.Same(ListVocabulary.All["collection"], ListVocabulary.For("no-such-question"));
    }

    [Fact]
    public void A_fence_that_opens_something_else_is_left_alone()
    {
        Assert.Empty(CollectionSyntax.Read("""
            :::infobox
            - Sonic
            :::

            :::collections
            - Tails
            :::
            """));
    }

    [Fact]
    public void What_it_reads_it_writes_back()
    {
        var block = CollectionSyntax.Read(Catalogue)[0];

        var source = CollectionSyntax.Write(block.Word, block.Argument, block.Items, ticked: false);
        var again = CollectionSyntax.Read(source)[0];

        Assert.Equal(source, CollectionSyntax.Write(again.Word, again.Argument, again.Items, ticked: false));
        Assert.Equal(block.Items.Select(i => i.Label), again.Items.Select(i => i.Label));
        Assert.Equal(block.Items[0].Variants.Select(v => v.Label),
            again.Items[0].Variants.Select(v => v.Label));
    }

    [Fact]
    public void Ticks_and_notes_survive_the_round_trip()
    {
        const string tally = """
            :::collection [[Override sprites]]
            - [x] Sonic — Gold, Sprite Day 2
              - [x] Base
              - [ ] Gold
            - [ ] Tails
            :::
            """;

        var block = CollectionSyntax.Read(tally)[0];
        var written = CollectionSyntax.Write(block.Word, block.Argument, block.Items, ticked: true);

        Assert.Equal(tally.ReplaceLineEndings("\n"), written);
    }

    [Fact]
    public void Rewriting_a_fence_leaves_the_page_around_it_alone()
    {
        var block = CollectionSyntax.Read(Catalogue)[0];

        var rewritten = CollectionSyntax.Replace(Catalogue, block,
            CollectionSyntax.Write(block.Word, block.Argument, [block.Items[0]], ticked: false));

        Assert.StartsWith("Sprites arrive on Thursdays.", rewritten, StringComparison.Ordinal);
        Assert.EndsWith("More prose after it.", rewritten, StringComparison.Ordinal);
        Assert.Single(CollectionSyntax.Read(rewritten)[0].Items);
    }

    /// <summary>The rule that makes linking optional and promotion safe: two linked
    /// items are their ids, and anything else is its text — so a tick made against
    /// <c>Sonic</c> keeps counting once Sonic becomes a page.</summary>
    [Fact]
    public void An_item_is_matched_by_id_where_both_have_one_and_by_text_otherwise()
    {
        var id = Guid.NewGuid();
        var linked = Item($"[Sonic](node://{id})");
        var promoted = Item($"[Sonic the Hedgehog](node://{id})");
        var plain = Item("Sonic");
        var other = Item($"[Sonic](node://{Guid.NewGuid()})");

        Assert.True(CollectionSyntax.Matches(linked, promoted));
        Assert.True(CollectionSyntax.Matches(linked, plain));
        Assert.True(CollectionSyntax.Matches(plain, linked));
        Assert.False(CollectionSyntax.Matches(linked, other));
        Assert.False(CollectionSyntax.Matches(plain, Item("Tails")));
    }

    [Fact]
    public void Matching_text_forgives_case_spacing_and_which_link_spelling_was_used()
    {
        Assert.True(CollectionSyntax.Matches(Item("[[Storm Scout]]"), Item("storm  scout")));
        Assert.True(CollectionSyntax.Matches(Item("Cheat Master"), Item("CHEAT MASTER")));
        Assert.Equal("Storm Scout", CollectionSyntax.PlainText("[[Storm Scout|Storm Scout]]"));
    }

    private static CollectionEntry Item(string label) =>
        CollectionSyntax.Read($":::collection x\n- {label}\n:::")[0].Items[0];
}
