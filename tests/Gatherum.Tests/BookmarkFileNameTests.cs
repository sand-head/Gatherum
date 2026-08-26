using System.Text;
using Gatherum.Core.Services;
using Gatherum.Infrastructure.Bookmarks;

namespace Gatherum.Tests;

/// <summary>What a bookmark gets called on disk. The page's own title is the name, the
/// way every file here is named for its title — and a title that cannot be a filename
/// verbatim is respelled rather than abandoned, because falling back to the host names
/// every capture from a site the same: "www.seriouseats.com (2).html" says nothing
/// about which recipe it holds.</summary>
public class BookmarkFileNameTests
{
    private static readonly Uri Recipe = new("https://www.seriouseats.com/pie-crust");

    [Fact]
    public void A_spellable_title_is_the_filename_verbatim() =>
        Assert.Equal("Closet thermals.html",
            HttpPageArchiver.HtmlFileName("Closet thermals", Recipe));

    [Theory]
    [InlineData("Perfect Pie Crust | Serious Eats", "Perfect Pie Crust Serious Eats.html")]
    [InlineData("Recipe: Overnight Focaccia", "Recipe Overnight Focaccia.html")]
    [InlineData("What is sous vide?", "What is sous vide.html")]
    [InlineData("Braising 101: Low & Slow / Every Time", "Braising 101 Low & Slow Every Time.html")]
    [InlineData("  \"Grandma's\" dumplings…  ", "Grandma's dumplings….html")]
    public void An_unspellable_title_is_respelled_not_traded_for_the_host(
        string title, string fileName) =>
        Assert.Equal(fileName, HttpPageArchiver.HtmlFileName(title, Recipe));

    [Fact]
    public void Only_a_title_with_nothing_spellable_falls_back_to_the_host() =>
        Assert.Equal("www.seriouseats.com.html",
            HttpPageArchiver.HtmlFileName("???", Recipe));

    [Fact]
    public void A_long_title_is_cut_to_the_byte_budget_not_abandoned()
    {
        var fileName = HttpPageArchiver.HtmlFileName(
            new string('a', 300) + " | Serious Eats", Recipe);
        Assert.EndsWith(".html", fileName);
        Assert.StartsWith("aaa", fileName);
        Assert.True(NodePaths.IsLegalSegment(fileName));
    }

    [Fact]
    public void Cutting_never_splits_a_character()
    {
        // Four bytes each in UTF-8: the cut has to land between clefs, not inside one.
        var fileName = HttpPageArchiver.HtmlFileName(
            string.Concat(Enumerable.Repeat("\U0001D11E", 100)) + ":", Recipe);
        Assert.True(Encoding.UTF8.GetByteCount(fileName) <= NodePaths.MaxSegmentBytes);
        Assert.True(NodePaths.IsLegalSegment(fileName));
        Assert.DoesNotContain('�', fileName);
    }

    [Fact]
    public void A_titleless_page_is_still_named_for_its_own_url_not_just_the_site()
    {
        // PageSnapshot titles a titleless page "host/path"; the slash respells to a
        // space instead of throwing the path away.
        Assert.Equal("www.seriouseats.com pie-crust.html",
            HttpPageArchiver.HtmlFileName("www.seriouseats.com/pie-crust", Recipe));
    }

    [Fact]
    public void A_reserved_stem_is_not_a_filename_even_respelled() =>
        Assert.Equal("www.seriouseats.com.html",
            HttpPageArchiver.HtmlFileName("CON", Recipe));
}
