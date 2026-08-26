using Gatherum.Infrastructure.Epub;

namespace Gatherum.Tests;

public class EpubBookTests
{
    private static async Task<EpubBook> OpenAsync(byte[] epub) =>
        await EpubBook.OpenAsync(new MemoryStream(epub));

    [Fact]
    public async Task The_navigation_document_names_the_chapters_the_spine_orders()
    {
        using var book = await OpenAsync(EpubFixtures.TwoChapterBook());

        Assert.Equal("Closet Thermals", book.Title);
        Assert.Equal(["OEBPS/ch1.xhtml", "OEBPS/ch2.xhtml"],
            book.Chapters.Select(c => c.EntryPath));
        Assert.Equal(["The Summer", "The Winter"], book.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task An_epub2_ncx_names_the_chapters_when_there_is_no_nav_document()
    {
        using var book = await OpenAsync(EpubFixtures.Zip(
            EpubFixtures.Text("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """),
            EpubFixtures.Text("content.opf", """
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="2.0">
                  <metadata><dc:title>Old Book</dc:title></metadata>
                  <manifest>
                    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                    <item id="c1" href="one.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine toc="ncx"><itemref idref="c1"/></spine>
                </package>
                """),
            EpubFixtures.Text("toc.ncx", """
                <?xml version="1.0"?>
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
                  <navMap><navPoint id="p1"><navLabel><text>Chapter the First</text></navLabel>
                    <content src="one.xhtml"/></navPoint></navMap>
                </ncx>
                """),
            EpubFixtures.Text("one.xhtml", "<html><body><p>words</p></body></html>")));

        Assert.Equal(["Chapter the First"], book.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task A_chapter_renders_self_contained_and_inert_with_the_pager_aboard()
    {
        using var book = await OpenAsync(EpubFixtures.TwoChapterBook());

        var html = await EpubChapterHtml.RenderAsync(book, 0);

        // The chapter's words and image made it, the image as bytes the frame already
        // holds; the book's script did not.
        Assert.Contains("The closet runs hot", html);
        Assert.Contains("data:image/png;base64,iVBORw==", html);
        Assert.DoesNotContain("alert(", html);
        // The stylesheet was folded in, its font with it.
        Assert.Contains("data:font/woff2;base64,", html);
        Assert.DoesNotContain("style.css", html);
        // Every link still goes where it can: the footnote stays a fragment, the
        // cross-reference asks the hosting page, the file link lost its href, the
        // external one kept it.
        Assert.Contains("href=\"#fn1\"", html);
        Assert.Contains("data-gatherum-chapter=\"1\"", html);
        Assert.DoesNotContain("href=\"pic.png\"", html);
        Assert.Contains("href=\"https://example.org/\"", html);
        // And the reader chrome is aboard.
        Assert.Contains("id=\"epub-flow\"", html);
        Assert.Contains("id=\"epub-page\"", html);
        Assert.Contains("gatherumEpubChapter", html);
    }

    [Fact]
    public async Task The_policy_admits_exactly_the_pager_as_the_rendering_serializes_it()
    {
        Assert.StartsWith("sandbox allow-scripts; default-src 'none';",
            EpubChapterHtml.ContentSecurityPolicy);

        // The hash must cover the script byte-for-byte as it lands in the rendered
        // chapter — one serialization quirk between them and the browser refuses the
        // pager, so the test hashes what was actually rendered.
        using var book = await OpenAsync(EpubFixtures.TwoChapterBook());
        var html = await EpubChapterHtml.RenderAsync(book, 0);
        var start = html.LastIndexOf("<script>", StringComparison.Ordinal) + "<script>".Length;
        var script = html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)];
        var hash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)));
        Assert.Contains($"script-src 'sha256-{hash}'", EpubChapterHtml.ContentSecurityPolicy);
    }
}
