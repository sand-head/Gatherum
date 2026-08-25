using System.Text;
using Gatherum.Infrastructure.Extraction;

namespace Gatherum.Tests;

public class HtmlTextExtractorTests
{
    private static async Task<string> ExtractAsync(string html)
    {
        var extractor = new HtmlTextExtractor();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(html));
        return await extractor.ExtractAsync(stream, "text/html", "page.html");
    }

    [Fact]
    public void Claims_html_however_it_arrives()
    {
        var extractor = new HtmlTextExtractor();
        Assert.True(extractor.CanExtract("text/html", "snapshot.html"));
        Assert.True(extractor.CanExtract("application/octet-stream", "old-page.htm"));
        Assert.False(extractor.CanExtract("text/plain", "notes.txt"));
        Assert.False(extractor.CanExtract("text/markdown", "page.md"));
    }

    [Fact]
    public async Task Words_survive_and_markup_scripts_and_styles_do_not()
    {
        var text = await ExtractAsync("""
            <html><head><title>Closet thermals</title>
            <style>body { color: red; }</style>
            <script>console.log("boot")</script></head>
            <body><h1>Summer</h1><p>the closet runs <b>hot</b></p></body></html>
            """);

        Assert.Contains("Closet thermals", text);
        Assert.Contains("the closet runs hot", text);
        Assert.Contains("Summer", text);
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("color: red", text);
        Assert.DoesNotContain("console.log", text);
    }

    [Fact]
    public async Task A_minified_page_still_reads_as_words_not_word_soup()
    {
        var text = await ExtractAsync(
            "<html><body><h1>Closet thermals</h1><p>runs hot</p><ul><li>add</li><li>fans</li></ul></body></html>");
        Assert.Contains("Closet thermals\nruns hot", text);
        Assert.Contains("add\nfans", text);
    }

    [Fact]
    public async Task Markup_whitespace_collapses_instead_of_indexing_as_gaps()
    {
        var text = await ExtractAsync(
            "<html><body>\n    <p>one\n        two</p>\n\n\n<p>three</p>\n  </body></html>");
        Assert.Contains("one\ntwo", text);
        Assert.DoesNotContain("  ", text);
    }
}
