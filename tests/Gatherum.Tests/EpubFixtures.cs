using System.IO.Compression;
using System.Text;

namespace Gatherum.Tests;

/// <summary>Books small enough to read in a test: a zip builder, and the one
/// two-chapter book most of the EPUB tests open.</summary>
internal static class EpubFixtures
{
    public static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = entry.Open();
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    public static (string Name, byte[] Content) Text(string name, string content) =>
        (name, Encoding.UTF8.GetBytes(content));

    /// <summary>Two chapters under OEBPS, named by an EPUB 3 nav document, wearing a
    /// stylesheet with a font, an image, a footnote, a cross-chapter link, and a script
    /// that has no business surviving.</summary>
    public static byte[] TwoChapterBook() => Zip(
        Text("mimetype", "application/epub+zip"),
        Text("META-INF/container.xml", """
            <?xml version="1.0"?>
            <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """),
        Text("OEBPS/content.opf", """
            <?xml version="1.0"?>
            <package xmlns="http://www.idpf.org/2007/opf" xmlns:dc="http://purl.org/dc/elements/1.1/" version="3.0">
              <metadata><dc:title>Closet Thermals</dc:title></metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="css" href="style.css" media-type="text/css"/>
                <item id="pic" href="pic.png" media-type="image/png"/>
                <item id="font" href="fonts/serif.woff2" media-type="font/woff2"/>
              </manifest>
              <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
            </package>
            """),
        Text("OEBPS/nav.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <body><nav epub:type="toc"><ol>
              <li><a href="ch1.xhtml">The Summer</a></li>
              <li><a href="ch2.xhtml">The Winter</a></li>
            </ol></nav></body></html>
            """),
        Text("OEBPS/ch1.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml"><head>
              <link rel="stylesheet" href="style.css"/>
              <script>alert('boo')</script>
            </head><body>
              <p>The closet runs hot<a href="#fn1">*</a> in July.</p>
              <p><img src="pic.png" alt="the rack"/></p>
              <p>Compare <a href="ch2.xhtml">the winter</a>, or the
                 <a href="pic.png">picture file</a>, or
                 <a href="https://example.org/">the forum</a>.</p>
              <aside id="fn1">Measured at the top shelf.</aside>
            </body></html>
            """),
        Text("OEBPS/ch2.xhtml", """
            <html xmlns="http://www.w3.org/1999/xhtml"><body>
              <p>In January it merely simmers.</p>
            </body></html>
            """),
        Text("OEBPS/style.css", """
            @font-face { font-family: Serif; src: url(fonts/serif.woff2); }
            body { font-family: Serif, serif; }
            """),
        ("OEBPS/pic.png", [0x89, 0x50, 0x4E, 0x47]),
        ("OEBPS/fonts/serif.woff2", [0x77, 0x4F, 0x46, 0x32]));
}
