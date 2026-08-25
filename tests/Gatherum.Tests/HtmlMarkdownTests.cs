using AngleSharp.Html.Parser;
using Gatherum.Infrastructure.Extraction;

namespace Gatherum.Tests;

/// <summary>The rendering a captured page reads as — to search, and to a model over
/// MCP. Prose-first: structure survives, markup does not, and a page minified to one
/// line comes out the same as its pretty-printed twin.</summary>
public class HtmlMarkdownTests
{
    private static string Render(string html)
    {
        var document = new HtmlParser().ParseDocument($"<html><body>{html}</body></html>");
        return HtmlMarkdown.Render(document.Body!);
    }

    [Fact]
    public void Headings_paragraphs_and_emphasis_read_as_markdown()
    {
        var markdown = Render(
            "<h2>Thermals</h2><p>the closet runs <b>hot</b> in <em>summer</em></p>");
        Assert.Equal("## Thermals\n\nthe closet runs **hot** in *summer*", markdown);
    }

    [Fact]
    public void A_minified_page_reads_the_same_as_a_pretty_printed_one()
    {
        var minified = Render("<h1>A</h1><p>one two</p><p>three</p>");
        var pretty = Render("\n  <h1>A</h1>\n  <p>one\n     two</p>\n\n  <p>three</p>\n");
        Assert.Equal(minified, pretty);
        Assert.Equal("# A\n\none two\n\nthree", minified);
    }

    [Fact]
    public void Links_keep_their_addresses_except_the_ones_that_go_nowhere()
    {
        var markdown = Render("""
            <p><a href="https://example.org/fans">the fans</a>,
            <a href="#top">back to top</a>, and <a href="https://example.org/bare"></a></p>
            """);
        Assert.Equal(
            "[the fans](https://example.org/fans), back to top, and <https://example.org/bare>",
            markdown);
    }

    [Fact]
    public void Lists_nest_by_indentation_and_ordered_ones_count()
    {
        var markdown = Render("""
            <ul><li>racks<ul><li>top shelf</li></ul></li><li>fans</li></ul>
            <ol start="3"><li>third</li><li>fourth</li></ol>
            """);
        Assert.Equal("- racks\n  - top shelf\n- fans\n\n3. third\n4. fourth", markdown);
    }

    [Fact]
    public void Quotes_code_and_rules_keep_their_shapes()
    {
        var markdown = Render("""
            <blockquote><p>it runs hot</p></blockquote>
            <pre><code class="language-sh">sensors | grep temp</code></pre>
            <hr>
            <p>after <code>the rule</code></p>
            """);
        Assert.Equal(
            "> it runs hot\n\n```sh\nsensors | grep temp\n```\n\n---\n\nafter `the rule`",
            markdown);
    }

    [Fact]
    public void Tables_come_out_as_tables()
    {
        var markdown = Render("""
            <table>
              <tr><th>Sensor</th><th>Reading</th></tr>
              <tr><td>closet</td><td>41 | hot</td></tr>
            </table>
            """);
        Assert.Equal(
            "| Sensor | Reading |\n| --- | --- |\n| closet | 41 \\| hot |",
            markdown);
    }

    [Fact]
    public void An_inlined_image_reduces_to_its_alt_text_not_a_wall_of_base64()
    {
        var markdown = Render("""
            <p><img src="data:image/png;base64,AAAA" alt="the rack"> and
            <img src="https://example.org/live.png" alt="live"></p>
            """);
        Assert.Equal("*[the rack]* and ![live](https://example.org/live.png)", markdown);
        Assert.DoesNotContain("base64", markdown);
    }

    [Fact]
    public void Scripts_styles_and_navigation_chrome_convert_to_their_content_only()
    {
        var markdown = Render("""
            <script>track()</script><style>p{color:red}</style>
            <nav><a href="https://example.org/">Home</a></nav>
            <p>a line<br>broken in two</p>
            """);
        Assert.Equal("[Home](https://example.org/)\n\na line\nbroken in two", markdown);
    }
}
