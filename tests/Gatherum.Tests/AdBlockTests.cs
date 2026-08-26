using System.Text;
using Gatherum.Infrastructure.Bookmarks;

namespace Gatherum.Tests;

/// <summary>The capture-time ad blocker: which hosts the list claims, and what a
/// snapshot does about them — nothing fetched from one, nothing kept pointing at one,
/// except on the one page that is legitimately theirs.</summary>
public class AdBlockTests
{
    private static readonly Uri Page = new("https://example.org/recipes/pie");
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_entry_claims_its_whole_subtree_and_nothing_beside_it()
    {
        var list = new AdBlocklist(["doubleclick.net", "# a comment", "", "ads.linkedin.com"]);
        Assert.Equal("doubleclick.net", list.Match("doubleclick.net"));
        Assert.Equal("doubleclick.net", list.Match("securepubads.g.doubleclick.net"));
        Assert.Equal("doubleclick.net", list.Match("AD.DOUBLECLICK.NET"));
        Assert.Null(list.Match("notdoubleclick.net"));
        Assert.Null(list.Match("doubleclick.net.evil.example"));
        Assert.Equal("ads.linkedin.com", list.Match("px.ads.linkedin.com"));
        Assert.Null(list.Match("www.linkedin.com"));
    }

    [Fact]
    public void The_packaged_list_loads_and_knows_the_usual_suspects()
    {
        var list = AdBlocklist.Packaged();
        Assert.False(list.IsEmpty);
        Assert.True(list.Blocks(new Uri("https://securepubads.g.doubleclick.net/tag/js/gpt.js")));
        Assert.True(list.Blocks(new Uri("https://cdn.taboola.com/libtrc/loader.js")));
        Assert.False(list.Blocks(new Uri("https://www.seriouseats.com/pie-crust")));
        Assert.False(list.Blocks(new Uri("https://fonts.googleapis.com/css2?family=Lora")));
    }

    [Fact]
    public async Task A_snapshot_neither_keeps_nor_fetches_what_a_listed_host_serves()
    {
        var asked = new List<string>();
        var assets = new Dictionary<string, FetchedAsset>
        {
            ["https://example.org/pic.png"] = new("image/png", [1, 2, 3]),
        };
        var result = await PageSnapshot.BuildAsync(Page, Encoding.UTF8.GetBytes("""
            <html><body>
              <img src="https://ad.doubleclick.net/pixel.png">
              <link rel="stylesheet" href="https://www.googletagmanager.com/style.css">
              <img src="/pic.png">
              <p>the recipe</p>
            </body></html>
            """), (url, _) =>
        {
            asked.Add(url.AbsoluteUri);
            return Task.FromResult(assets.GetValueOrDefault(url.AbsoluteUri));
        }, CapturedAt, AdBlocklist.Packaged());

        var html = Encoding.UTF8.GetString(result.Content);
        Assert.Contains("the recipe", html);
        // The tracking pixel is not in the file — a live link would report every
        // reading of the archive — and was never even asked for.
        Assert.DoesNotContain("doubleclick", html);
        Assert.DoesNotContain("googletagmanager", html);
        Assert.DoesNotContain(asked,
            url => url.Contains("doubleclick") || url.Contains("googletagmanager"));
        // The page's own image folded in as always.
        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public async Task A_page_on_a_listed_host_keeps_its_own_things_and_loses_the_rest()
    {
        var result = await PageSnapshot.BuildAsync(new Uri("https://blog.taboola.com/post"),
            Encoding.UTF8.GetBytes("""
                <html><body>
                  <img src="https://cdn.taboola.com/logo.png">
                  <img src="https://ad.doubleclick.net/pixel.png">
                  <p>an announcement</p>
                </body></html>
                """),
            (_, _) => Task.FromResult<FetchedAsset?>(null),
            CapturedAt, AdBlocklist.Packaged());

        var html = Encoding.UTF8.GetString(result.Content);
        // Bookmarking the ad company is bookmarking a page like any other …
        Assert.Contains("cdn.taboola.com/logo.png", html);
        // … but somebody else's ads are still nobody's content.
        Assert.DoesNotContain("doubleclick", html);
    }
}
