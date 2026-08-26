using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Domain;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>EPUB search text as the book's Markdown rendering: chapters in spine
/// order — the order the book reads in, not the order the zip happens to store
/// them — each converted the way <see cref="HtmlTextExtractor"/> converts a page,
/// with the title from the package metadata leading as a heading. A book whose
/// package document is missing or broken still extracts: every HTML-looking entry,
/// in archive order.</summary>
public class EpubTextExtractor : ITextExtractor
{
    private const int MaxChars = 4 * 1024 * 1024;

    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    public bool CanExtract(string mediaType, string fileName) =>
        mediaType.Equals(MediaTypes.Epub, StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(fileName).Equals(".epub", StringComparison.OrdinalIgnoreCase);

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        // ZipArchive needs a seekable stream; uploads arrive forward-only.
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;

        using var archive = new ZipArchive(buffered, ZipArchiveMode.Read);
        var (title, chapters) = ReadPackage(archive);

        var parser = new HtmlParser();
        var text = new StringBuilder();
        if (title?.Trim() is { Length: > 0 } heading)
            text.Append("# ").Append(heading);

        foreach (var entry in chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var chapter = entry.Open();
            var document = await parser.ParseDocumentAsync(chapter, cancellationToken);
            if (document.Body is not { } body || HtmlMarkdown.Render(body) is not { Length: > 0 } markdown)
                continue;
            if (text.Length > 0)
                text.Append("\n\n");
            text.Append(markdown);
            if (text.Length > MaxChars)
                break;
        }

        var result = text.ToString();
        return result.Length <= MaxChars ? result : result[..MaxChars];
    }

    /// <summary>The package document (OPF), found through META-INF/container.xml, names
    /// the book and orders its reading. Anything wrong with either file falls back to
    /// the archive itself rather than failing the extraction.</summary>
    private static (string? Title, IReadOnlyList<ZipArchiveEntry> Chapters) ReadPackage(ZipArchive archive)
    {
        try
        {
            if (OpfPath(archive) is { } opfPath && archive.GetEntry(opfPath) is { } opfEntry)
            {
                using var stream = opfEntry.Open();
                var opf = XDocument.Load(stream);
                var ns = opf.Root!.Name.Namespace;
                var title = opf.Root.Element(ns + "metadata")?.Element(Dc + "title")?.Value;

                var manifest = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var item in opf.Root.Element(ns + "manifest")?.Elements(ns + "item") ?? [])
                {
                    if ((string?)item.Attribute("id") is { } id)
                        manifest[id] = item;
                }

                var directory = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
                var chapters = new List<ZipArchiveEntry>();
                foreach (var itemref in opf.Root.Element(ns + "spine")?.Elements(ns + "itemref") ?? [])
                {
                    if ((string?)itemref.Attribute("idref") is not { } idref
                        || !manifest.TryGetValue(idref, out var item)
                        || (string?)item.Attribute("href") is not { } href
                        || !IsHtml((string?)item.Attribute("media-type"), href))
                        continue;
                    if (archive.GetEntry(Resolve(directory, href)) is { } entry)
                        chapters.Add(entry);
                }
                if (chapters.Count > 0)
                    return (title, chapters);
            }
        }
        catch (Exception e) when (e is XmlException or InvalidDataException or NullReferenceException)
        {
        }
        return (null, archive.Entries.Where(e => IsHtml(mediaType: null, e.FullName)).ToList());
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

    private static bool IsHtml(string? mediaType, string href) =>
        mediaType is "application/xhtml+xml" or "text/html" ||
        (mediaType is null && Path.GetExtension(href.Split('#', '?')[0]).ToLowerInvariant()
            is ".xhtml" or ".html" or ".htm");

    /// <summary>A manifest href is a URL relative to the package document; a zip entry
    /// name is neither encoded nor relative, so decode and collapse the segments.</summary>
    private static string Resolve(string directory, string href)
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
