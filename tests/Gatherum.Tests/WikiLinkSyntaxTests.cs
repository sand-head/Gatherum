using Gatherum.Core.Markdown;

namespace Gatherum.Tests;

public class WikiLinkSyntaxTests
{
    [Fact]
    public void Both_spellings_name_the_target()
    {
        var targets = WikiLinkSyntax.Targets(
            "See [[Quadlet notes]] and [[Homelab|the rack]] for the rest.");

        Assert.Equal(["Homelab", "Quadlet notes"], targets.Order());
    }

    [Fact]
    public void A_table_cell_separates_on_the_escaped_pipe()
    {
        Assert.Equal(["Homelab"], WikiLinkSyntax.Targets("| Where | [[Homelab\\|the rack]] |"));
    }

    [Fact]
    public void Code_is_not_a_link()
    {
        var targets = WikiLinkSyntax.Targets(
            """
            Write `[[Not a link]]` to show the syntax:

            ```md
            [[Also not a link]]
            ```

            But [[This one]] is.
            """);

        Assert.Equal(["This one"], targets);
    }

    [Fact]
    public void Half_written_links_are_text()
    {
        Assert.Empty(WikiLinkSyntax.Targets("[[unclosed and [[a[b]] and [[]] and [[ | x]]"));
    }

    [Fact]
    public void The_same_title_twice_is_one_target()
    {
        Assert.Single(WikiLinkSyntax.Targets("[[Homelab]] and [[homelab]] again"));
    }
}
