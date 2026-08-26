using System.Net;
using System.Net.Http;
using System.Text;
using Gatherum.Infrastructure.Bookmarks;
using Microsoft.Extensions.Logging.Abstractions;

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

        // With a host listed exactly *and* by parent, the parent is the answer: equal
        // answers for one outfit's hosts are what the first-party exemption rests on.
        var both = new AdBlocklist(["ad.doubleclick.net", "doubleclick.net"]);
        Assert.Equal("doubleclick.net", both.Match("ad.doubleclick.net"));
    }

    [Fact]
    public void Parse_reads_the_formats_community_lists_come_in()
    {
        var list = AdBlocklist.Parse(
            """
            # a hosts-file comment
            ! an Adblock comment
            0.0.0.0 ads.example.com
            127.0.0.1 tracker.example.net # trailing comment
            0.0.0.0 localhost
            plain.example.org
            *.wild.example.com
            ||abp.example.com^
            ||abp-options.example.com^$third-party
            @@||excepted.example.com^
            cosmetic.example.com##.ad-banner
            /banner/*/ad.js
            """.Split('\n'));

        Assert.Equal("ads.example.com", list.Match("ads.example.com"));
        Assert.Equal("tracker.example.net", list.Match("cdn.tracker.example.net"));
        Assert.Equal("plain.example.org", list.Match("plain.example.org"));
        Assert.Equal("wild.example.com", list.Match("a.wild.example.com"));
        Assert.Equal("abp.example.com", list.Match("abp.example.com"));
        Assert.Equal("abp-options.example.com", list.Match("abp-options.example.com"));
        // Exceptions, cosmetic rules and path filters are beyond a host blocker —
        // skipped whole, never misread as the domain they mention.
        Assert.Null(list.Match("excepted.example.com"));
        Assert.Null(list.Match("cosmetic.example.com"));
        // And the localhost lines every hosts file carries do not block loopback.
        Assert.Null(list.Match("localhost"));
    }

    [Fact]
    public async Task The_community_list_is_fetched_once_and_blocks_what_it_names()
    {
        var wire = new CountingWire(() => Ok("0.0.0.0 ads.example.com\n"));
        var clock = new ManualClock();
        var provider = new AdBlocklistProvider("https://lists.example/hosts", wire, clock,
            NullLogger<AdBlocklistProvider>.Instance);

        var list = await provider.CurrentAsync();
        Assert.True(list.Blocks(new Uri("https://ads.example.com/pixel")));
        // The packaged entries ride along: an update can widen blocking, never narrow it.
        Assert.True(list.Blocks(new Uri("https://ad.doubleclick.net/tag")));
        Assert.False(list.Blocks(new Uri("https://www.seriouseats.com/pie-crust")));

        clock.Advance(TimeSpan.FromHours(1));
        await provider.CurrentAsync();
        Assert.Equal(1, wire.Requests);

        // A day on, the list is stale and the next capture refreshes it.
        clock.Advance(TimeSpan.FromHours(24));
        await provider.CurrentAsync();
        Assert.Equal(2, wire.Requests);
    }

    [Fact]
    public async Task A_failed_fetch_blocks_with_the_packaged_list_and_does_not_retry_every_capture()
    {
        var wire = new CountingWire(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var clock = new ManualClock();
        var provider = new AdBlocklistProvider("https://lists.example/hosts", wire, clock,
            NullLogger<AdBlocklistProvider>.Instance);

        var list = await provider.CurrentAsync();
        Assert.True(list.Blocks(new Uri("https://ad.doubleclick.net/pixel")));

        clock.Advance(TimeSpan.FromMinutes(5));
        await provider.CurrentAsync();
        Assert.Equal(1, wire.Requests);

        clock.Advance(TimeSpan.FromMinutes(15));
        await provider.CurrentAsync();
        Assert.Equal(2, wire.Requests);
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>The "web" for provider tests: counts fetches, answers as told.</summary>
    private sealed class CountingWire(Func<HttpResponseMessage> respond)
        : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(respond());
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
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

    [Fact]
    public async Task A_community_list_naming_subdomains_does_not_break_the_exemption()
    {
        // The way a fetched list arrives in practice: exact hosts, one by one, unioned
        // with the packaged registrable domains.
        var list = AdBlocklist
            .Parse(["0.0.0.0 cdn.taboola.com", "0.0.0.0 blog.taboola.com"])
            .Union(AdBlocklist.Packaged());
        var result = await PageSnapshot.BuildAsync(new Uri("https://blog.taboola.com/post"),
            Encoding.UTF8.GetBytes("""
                <html><body><img src="https://cdn.taboola.com/logo.png"></body></html>
                """),
            (_, _) => Task.FromResult<FetchedAsset?>(null),
            CapturedAt, list);

        Assert.Contains("cdn.taboola.com/logo.png", Encoding.UTF8.GetString(result.Content));
    }
}
