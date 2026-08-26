using System.IO.Compression;
using System.Text;
using Gatherum.Infrastructure.Extraction;

namespace Gatherum.Tests;

public class EpubTextExtractorTests
{
    private static MemoryStream Epub(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static async Task<string> ExtractAsync(MemoryStream epub)
    {
        var extractor = new EpubTextExtractor();
        return await extractor.ExtractAsync(epub, "application/epub+zip", "book.epub");
    }

    [Fact]
    public void Claims_epubs_however_they_arrive()
    {
        var extractor = new EpubTextExtractor();
        Assert.True(extractor.CanExtract("application/epub+zip", "book.epub"));
        Assert.True(extractor.CanExtract("application/octet-stream", "book.epub"));
        Assert.False(extractor.CanExtract("application/zip", "archive.zip"));
    }

    [Fact]
    public async Task Chapters_extract_in_spine_order_led_by_the_books_title()
    {
        // The zip stores chapter two first; the spine says chapter one reads first.
        using var epub = Epub(
            ("mimetype", "application/epub+zip"),
            ("OEBPS/ch2.xhtml", "<html><body><p>Second chapter</p></body></html>"),
            ("OEBPS/ch1.xhtml", "<html><body><p>First chapter</p></body></html>"),
            ("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """),
            ("OEBPS/content.opf", """
                <?xml version="1.0"?>
                <package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0">
                  <metadata><dc:title>Closet Thermals</dc:title></metadata>
                  <manifest>
                    <item id="c1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c2" href="ch2.xhtml" media-type="application/xhtml+xml"/>
                    <item id="cover" href="cover.jpg" media-type="image/jpeg"/>
                  </manifest>
                  <spine><itemref idref="c1"/><itemref idref="c2"/><itemref idref="cover"/></spine>
                </package>
                """));

        var text = await ExtractAsync(epub);

        Assert.Equal("# Closet Thermals\n\nFirst chapter\n\nSecond chapter", text);
    }

    [Fact]
    public async Task A_broken_package_still_yields_the_chapters_the_zip_holds()
    {
        using var epub = Epub(
            ("mimetype", "application/epub+zip"),
            ("OEBPS/ch1.xhtml", "<html><body><p>Still findable</p></body></html>"),
            ("OEBPS/style.css", "p { color: red; }"));

        Assert.Equal("Still findable", await ExtractAsync(epub));
    }
}
