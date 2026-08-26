using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;

namespace Gatherum.Infrastructure.Epub;

/// <summary>A chapter as the spine orders it: where its file lives in the zip, and what
/// the book's own table of contents calls it — null when the contents never name it.</summary>
public sealed record EpubChapter(string EntryPath, string? Title);

/// <summary>An EPUB, opened for reading: the title and chapter list the package
/// document declares, and the chapter files themselves. The package document (OPF),
/// found through META-INF/container.xml, orders the reading; the navigation document
/// (EPUB 3) or NCX (EPUB 2) names the chapters. A book whose package document is
/// missing or broken still opens: every HTML-looking entry, in archive order,
/// nameless.</summary>
public sealed class EpubBook : IDisposable
{
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    private readonly ZipArchive archive;
    private readonly MemoryStream? owned;

    public string? Title { get; }
    public IReadOnlyList<EpubChapter> Chapters { get; }

    private EpubBook(ZipArchive archive, MemoryStream? owned, string? title,
        IReadOnlyList<EpubChapter> chapters)
    {
        this.archive = archive;
        this.owned = owned;
        Title = title;
        Chapters = chapters;
    }

    public void Dispose()
    {
        archive.Dispose();
        owned?.Dispose();
    }

    public static async Task<EpubBook> OpenAsync(Stream content,
        CancellationToken cancellationToken = default)
    {
        // ZipArchive needs a seekable stream; uploads arrive forward-only. A stream
        // that can already seek — a stored file — is read in place.
        MemoryStream? owned = null;
        if (!content.CanSeek)
        {
            owned = new MemoryStream();
            await content.CopyToAsync(owned, cancellationToken);
            owned.Position = 0;
            content = owned;
        }

        var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: owned is null);
        var (title, chapters) = await ReadPackageAsync(archive, cancellationToken);
        return new EpubBook(archive, owned, title, chapters);
    }

    /// <summary>The chapter's file as the book carries it — for extraction, or as the
    /// raw material a rendering starts from.</summary>
    public Stream OpenChapter(int index) =>
        archive.GetEntry(Chapters[index].EntryPath)!.Open();

    internal ZipArchiveEntry? Entry(string path) => archive.GetEntry(path);

    private static async Task<(string? Title, IReadOnlyList<EpubChapter> Chapters)>
        ReadPackageAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        try
        {
            if (OpfPath(archive) is { } opfPath && archive.GetEntry(opfPath) is { } opfEntry)
            {
                XDocument opf;
                using (var stream = opfEntry.Open())
                    opf = XDocument.Load(stream);
                var ns = opf.Root!.Name.Namespace;
                var title = opf.Root.Element(ns + "metadata")?.Element(Dc + "title")?.Value;

                var manifest = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var item in opf.Root.Element(ns + "manifest")?.Elements(ns + "item") ?? [])
                {
                    if ((string?)item.Attribute("id") is { } id)
                        manifest[id] = item;
                }

                var directory = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
                var names = await ChapterNamesAsync(archive, opf, manifest, directory,
                    cancellationToken);
                var chapters = new List<EpubChapter>();
                foreach (var itemref in opf.Root.Element(ns + "spine")?.Elements(ns + "itemref") ?? [])
                {
                    if ((string?)itemref.Attribute("idref") is not { } idref
                        || !manifest.TryGetValue(idref, out var item)
                        || (string?)item.Attribute("href") is not { } href
                        || !IsHtml((string?)item.Attribute("media-type"), href))
                        continue;
                    var path = Resolve(directory, href);
                    if (archive.GetEntry(path) is not null)
                        chapters.Add(new EpubChapter(path,
                            names.GetValueOrDefault(path)));
                }
                if (chapters.Count > 0)
                    return (title, chapters);
            }
        }
        catch (Exception e) when (e is XmlException or InvalidDataException or NullReferenceException)
        {
        }
        return (null, archive.Entries
            .Where(e => IsHtml(mediaType: null, e.FullName))
            .Select(e => new EpubChapter(e.FullName, Title: null))
            .ToList());
    }

    /// <summary>What the book's own table of contents calls each file: the EPUB 3
    /// navigation document when the manifest marks one, the EPUB 2 NCX otherwise. A
    /// file the contents list more than once keeps the first name — the outermost
    /// entry, the way a contents page reads.</summary>
    private static async Task<Dictionary<string, string>> ChapterNamesAsync(ZipArchive archive,
        XDocument opf, Dictionary<string, XElement> manifest, string directory,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var ns = opf.Root!.Name.Namespace;

        var nav = manifest.Values.FirstOrDefault(item =>
            ((string?)item.Attribute("properties") ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav"));
        if (nav is not null && (string?)nav.Attribute("href") is { } navHref
            && archive.GetEntry(Resolve(directory, navHref)) is { } navEntry)
        {
            var navDirectory = Path.GetDirectoryName(Resolve(directory, navHref))
                ?.Replace('\\', '/') ?? "";
            await using var stream = navEntry.Open();
            var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
            var toc = document.QuerySelectorAll("nav")
                .FirstOrDefault(n => n.GetAttribute("epub:type") == "toc")
                ?? document.QuerySelector("nav");
            foreach (var anchor in (toc ?? document.DocumentElement).QuerySelectorAll("a[href]"))
            {
                var path = Resolve(navDirectory, anchor.GetAttribute("href")!);
                if (anchor.TextContent.Trim() is { Length: > 0 } name)
                    names.TryAdd(path, name);
            }
            if (names.Count > 0)
                return names;
        }

        var tocId = (string?)opf.Root.Element(ns + "spine")?.Attribute("toc");
        if (tocId is not null && manifest.TryGetValue(tocId, out var ncxItem)
            && (string?)ncxItem.Attribute("href") is { } ncxHref
            && archive.GetEntry(Resolve(directory, ncxHref)) is { } ncxEntry)
        {
            var ncxDirectory = Path.GetDirectoryName(Resolve(directory, ncxHref))
                ?.Replace('\\', '/') ?? "";
            XDocument ncx;
            using (var stream = ncxEntry.Open())
                ncx = XDocument.Load(stream);
            foreach (var point in ncx.Descendants(Ncx + "navPoint"))
            {
                var src = (string?)point.Element(Ncx + "content")?.Attribute("src");
                var name = point.Element(Ncx + "navLabel")?.Element(Ncx + "text")?.Value.Trim();
                if (src is not null && name is { Length: > 0 })
                    names.TryAdd(Resolve(ncxDirectory, src), name);
            }
        }
        return names;
    }

    private static string? OpfPath(ZipArchive archive)
    {
        if (archive.GetEntry("META-INF/container.xml") is not { } entry)
            return null;
        using var stream = entry.Open();
        return XDocument.Load(stream).Root
            ?.Element(Container + "rootfiles")
            ?.Elements(Container + "rootfile")
            .Select(r => (string?)r.Attribute("full-path"))
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    internal static bool IsHtml(string? mediaType, string href) =>
        mediaType is "application/xhtml+xml" or "text/html" ||
        (mediaType is null && Path.GetExtension(href.Split('#', '?')[0]).ToLowerInvariant()
            is ".xhtml" or ".html" or ".htm");

    /// <summary>A manifest href is a URL relative to the document carrying it; a zip
    /// entry name is neither encoded nor relative, so decode and collapse the
    /// segments.</summary>
    internal static string Resolve(string directory, string href)
    {
        var path = Uri.UnescapeDataString(href.Split('#', '?')[0]);
        var segments = new List<string>();
        foreach (var segment in $"{directory}/{path}".Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == ".." && segments.Count > 0)
                segments.RemoveAt(segments.Count - 1);
            else if (segment != "..")
                segments.Add(segment);
        }
        return string.Join('/', segments);
    }
}
