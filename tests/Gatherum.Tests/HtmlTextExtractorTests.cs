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
    public async Task A_page_extracts_as_its_markdown_rendering_led_by_its_title()
    {
        var text = await ExtractAsync("""
            <html><head><title>Closet thermals</title>
            <style>body { color: red; }</style>
            <script>console.log("boot")</script></head>
            <body><h2>Summer</h2><p>the closet runs <b>hot</b></p></body></html>
            """);

        Assert.Equal("# Closet thermals\n\n## Summer\n\nthe closet runs **hot**", text);
    }

    [Fact]
    public async Task A_page_that_opens_with_its_own_title_does_not_say_it_twice()
    {
        var text = await ExtractAsync(
            "<html><head><title>Thermals</title></head><body><h1>Thermals</h1><p>hot</p></body></html>");
        Assert.Equal("# Thermals\n\nhot", text);
    }
}
