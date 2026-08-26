using System.Security.Cryptography;
using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Gatherum.Infrastructure.Bookmarks;

namespace Gatherum.Infrastructure.Epub;

/// <summary>Renders one chapter as the page the reader's frame shows: self-contained —
/// the book's images, stylesheets and fonts folded in as data: URIs, since the frame it
/// renders in can reach nothing else — inert the way a bookmark snapshot is, and wearing
/// the paginated reader chrome: content flowed into viewport-wide columns, one column a
/// page, turned by the one script the response's policy admits by hash. Links keep
/// going somewhere: a footnote pages within the chapter, a cross-reference asks the
/// hosting page (over postMessage) to open the chapter it names, an external link says
/// where it pointed.</summary>
public static class EpubChapterHtml
{
    public static async Task<string> RenderAsync(EpubBook book, int index,
        CancellationToken cancellationToken = default)
    {
        var chapter = book.Chapters[index];
        var directory = Path.GetDirectoryName(chapter.EntryPath)?.Replace('\\', '/') ?? "";

        IDocument document;
        await using (var stream = book.OpenChapter(index))
            document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);

        PageSnapshot.RemoveActiveContent(document);
        InlineResources(document, book, directory);
        RewriteLinks(document, book, directory);
        PageSnapshot.DeclareUtf8(document);
        InjectReader(document);
        return document.ToHtml();
    }

    /// <summary>What the chapter response's Content-Security-Policy allows: nothing but
    /// what the rendering folded in, scripts sandboxed to an opaque origin, and only the
    /// pager admitted — by hash, so a script that survived stripping still does not
    /// run.</summary>
    public static string ContentSecurityPolicy { get; } =
        "sandbox allow-scripts; default-src 'none'; img-src data:; media-src data:; " +
        "font-src data:; style-src 'unsafe-inline'; " +
        $"script-src 'sha256-{Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(PagerScript)))}'";

    private static void InlineResources(IDocument document, EpubBook book, string directory)
    {
        foreach (var link in document.QuerySelectorAll("link[rel~='stylesheet'][href]").ToList())
        {
            var href = link.GetAttribute("href")!;
            var path = HasScheme(href) ? null : EpubBook.Resolve(directory, href);
            if (path is null || ReadEntry(book, path) is not { } sheet)
            {
                link.Remove();
                continue;
            }
            var style = document.CreateElement("style");
            if (link.GetAttribute("media") is { } media)
                style.SetAttribute("media", media);
            // The sheet's own relative references resolve against the sheet, not the
            // chapter.
            var sheetDirectory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            style.TextContent = InlineCss(Encoding.UTF8.GetString(sheet), book, sheetDirectory);
            link.Parent?.ReplaceChild(style, link);
        }

        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = InlineCss(style.TextContent, book, directory);

        foreach (var element in document.All)
        {
            if (element.GetAttribute("style") is { } inline)
                element.SetAttribute("style", InlineCss(inline, book, directory));
            // The inlined bytes are the page; a srcset would reach for the web the
            // policy already refuses.
            element.RemoveAttribute("srcset");
        }

        foreach (var image in document.QuerySelectorAll("img[src], source[src]"))
        {
            if (DataUri(book, directory, image.GetAttribute("src")!) is { } data)
                image.SetAttribute("src", data);
        }
        // SVG's spelling of the same thing, in both its vocabularies.
        foreach (var image in document.QuerySelectorAll("image"))
        {
            foreach (var name in (string[])["href", "xlink:href"])
            {
                if (image.GetAttribute(name) is { } reference
                    && DataUri(book, directory, reference) is { } data)
                    image.SetAttribute(name, data);
            }
        }
    }

    private static string InlineCss(string css, EpubBook book, string directory)
    {
        return PageSnapshot.CssUrl().Replace(css, match =>
        {
            var quote = match.Groups["q"].Value;
            return DataUri(book, directory, match.Groups["url"].Value) is { } data
                ? $"url({quote}{data}{quote})"
                : match.Value;
        });
    }

    private static string? DataUri(EpubBook book, string directory, string reference)
    {
        if (reference.StartsWith('#') || HasScheme(reference))
            return null;
        var path = EpubBook.Resolve(directory, reference);
        return ReadEntry(book, path) is { } bytes
            ? $"data:{MediaTypeOf(path)};base64,{Convert.ToBase64String(bytes)}"
            : null;
    }

    private static byte[]? ReadEntry(EpubBook book, string path)
    {
        if (book.Entry(path) is not { } entry)
            return null;
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Every link still goes somewhere it can: a bare fragment stays a footnote
    /// jump the pager handles, a link into another chapter becomes a request to the
    /// hosting page, a link with a scheme keeps saying where it pointed — and a link to
    /// a file that is not a chapter has nowhere left to go, so the words stay and the
    /// link does not.</summary>
    private static void RewriteLinks(IDocument document, EpubBook book, string directory)
    {
        var chapters = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < book.Chapters.Count; i++)
            chapters.TryAdd(book.Chapters[i].EntryPath, i);

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href")!.Trim();
            if (href.StartsWith('#') || HasScheme(href))
                continue;
            if (chapters.TryGetValue(EpubBook.Resolve(directory, href), out var target))
            {
                anchor.SetAttribute("href", "#");
                anchor.SetAttribute("data-gatherum-chapter",
                    target.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                anchor.RemoveAttribute("href");
            }
        }
    }

    private static void InjectReader(IDocument document)
    {
        // A book's own <base> would re-aim every reference this rendering just settled.
        foreach (var baseElement in document.QuerySelectorAll("base").ToList())
            baseElement.Remove();

        if (document.Head is { } head)
        {
            if (document.QuerySelector("meta[name='viewport']") is null)
            {
                var viewport = document.CreateElement("meta");
                viewport.SetAttribute("name", "viewport");
                viewport.SetAttribute("content", "width=device-width, initial-scale=1");
                head.AppendChild(viewport);
            }
            var style = document.CreateElement("style");
            style.TextContent = ReaderStyle;
            head.AppendChild(style);
        }

        if (document.Body is not { } body)
            return;
        var box = document.CreateElement("div");
        box.Id = "epub-box";
        var flow = document.CreateElement("div");
        flow.Id = "epub-flow";
        while (body.FirstChild is { } child)
            flow.AppendChild(child);
        box.AppendChild(flow);
        body.AppendChild(box);

        var previous = document.CreateElement("button");
        previous.Id = "epub-prev";
        previous.SetAttribute("aria-label", "Previous page");
        previous.TextContent = "‹";
        var next = document.CreateElement("button");
        next.Id = "epub-next";
        next.SetAttribute("aria-label", "Next page");
        next.TextContent = "›";
        var page = document.CreateElement("div");
        page.Id = "epub-page";
        body.AppendChild(previous);
        body.AppendChild(next);
        body.AppendChild(page);

        var script = document.CreateElement("script");
        script.TextContent = PagerScript;
        body.AppendChild(script);
    }

    private static string MediaTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".svg" => "image/svg+xml",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    private static bool HasScheme(string value)
    {
        var colon = value.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(value[0]))
            return false;
        for (var i = 1; i < colon; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]) && value[i] is not ('+' or '-' or '.'))
                return false;
        }
        return true;
    }

    /// <summary>The reader's own dress, layered over whatever the book wears: the page
    /// is a fixed viewport, the chapter flows through viewport-wide columns behind it,
    /// and the chrome — page turns at the edges, the count at the foot — sits outside
    /// the text's box so the book's own styles never collide with it.</summary>
    private const string ReaderStyle = """
        html, body { height: 100% !important; }
        body { margin: 0 !important; overflow: hidden !important; background: #f8f5ef; }
        #epub-box { position: absolute; inset: 0; padding: 2.75rem 4rem 3.25rem;
                    overflow: hidden; box-sizing: border-box; }
        #epub-flow { position: relative; height: 100%; column-fill: auto; column-gap: 5rem;
                     transition: transform 0.2s ease; overflow-wrap: break-word; }
        #epub-flow img, #epub-flow svg, #epub-flow video {
            max-width: 100% !important; max-height: 100% !important; object-fit: contain; }
        #epub-prev, #epub-next { position: absolute; top: 0; bottom: 0; width: 3.5rem;
            border: 0; background: none; cursor: pointer; padding: 0;
            font: 2.2rem/1 system-ui, sans-serif; color: rgb(0 0 0 / 0.35);
            opacity: 0; transition: opacity 0.15s ease; }
        #epub-prev { left: 0; }
        #epub-next { right: 0; }
        #epub-prev:hover, #epub-next:hover,
        #epub-prev:focus-visible, #epub-next:focus-visible { opacity: 1; }
        @media (hover: none) { #epub-prev, #epub-next { opacity: 0.45; } }
        #epub-page { position: absolute; left: 0; right: 0; bottom: 1rem; text-align: center;
                     font: 0.8rem/1 system-ui, sans-serif; color: rgb(0 0 0 / 0.45);
                     pointer-events: none; }
        """;

    /// <summary>The pager: columns the chapter to the viewport, turns pages by
    /// translating the flow, and keeps the count at the foot honest. Also the reader's
    /// half of every link the rendering kept: fragments page to their target,
    /// cross-chapter links are announced to the hosting page over postMessage — the one
    /// direction a sandboxed frame can still speak.</summary>
    private const string PagerScript = """
        (() => {
          'use strict';
          const flow = document.getElementById('epub-flow');
          const label = document.getElementById('epub-page');
          let page = 0, pages = 1, step = 1;

          const show = (n) => {
            page = Math.max(0, Math.min(n, pages - 1));
            flow.style.transform = 'translateX(' + (-page * step) + 'px)';
            label.textContent = (page + 1) + ' / ' + pages;
            parent.postMessage({ gatherumEpubProgress: pages > 1 ? page / (pages - 1) : 0 }, '*');
          };
          // Where the reader left off arrives as a fragment — pages renumber with
          // every viewport, so it is a fraction, cashed in once the count is known.
          let restore = parseFloat((location.hash.match(/^#at=([0-9.]+)$/) || [])[1]);
          const layout = () => {
            const width = flow.clientWidth;
            const gap = parseFloat(getComputedStyle(flow).columnGap) || 0;
            flow.style.columnWidth = width + 'px';
            step = width + gap;
            pages = Math.max(1, Math.round((flow.scrollWidth + gap) / step));
            if (Number.isFinite(restore)) {
              page = Math.round(Math.min(restore, 1) * (pages - 1));
              restore = NaN;
            }
            show(page);
          };

          document.getElementById('epub-prev').addEventListener('click', () => show(page - 1));
          document.getElementById('epub-next').addEventListener('click', () => show(page + 1));
          addEventListener('keydown', (e) => {
            if (e.key === 'ArrowRight' || e.key === 'PageDown' || (e.key === ' ' && !e.shiftKey)) {
              e.preventDefault(); show(page + 1);
            } else if (e.key === 'ArrowLeft' || e.key === 'PageUp' || (e.key === ' ' && e.shiftKey)) {
              e.preventDefault(); show(page - 1);
            } else if (e.key === 'Home') { show(0); }
            else if (e.key === 'End') { show(pages - 1); }
          });
          let turned = 0;
          addEventListener('wheel', (e) => {
            const now = Date.now();
            if (now - turned < 250) return;
            turned = now;
            show(page + (e.deltaY > 0 || e.deltaX > 0 ? 1 : -1));
          }, { passive: true });

          document.addEventListener('click', (e) => {
            const anchor = e.target.closest('a');
            if (!anchor) return;
            const chapter = anchor.dataset.gatherumChapter;
            const href = anchor.getAttribute('href') || '';
            if (chapter !== undefined) {
              e.preventDefault();
              parent.postMessage({ gatherumEpubChapter: Number(chapter) }, '*');
            } else if (href.startsWith('#')) {
              e.preventDefault();
              const target = document.getElementById(decodeURIComponent(href.slice(1)));
              if (target) show(Math.floor(target.offsetLeft / step));
            }
          });

          addEventListener('resize', layout);
          addEventListener('load', layout);
          layout();
        })();
        """;
}
