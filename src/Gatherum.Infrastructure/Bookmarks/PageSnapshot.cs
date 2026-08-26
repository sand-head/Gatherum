using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Gatherum.Infrastructure.Bookmarks;

/// <summary>An asset a snapshot pulled in — a stylesheet or an image — with the media
/// type the server claimed for it. Null from the fetcher means "leave the reference
/// pointing at the live web": a capture with one dead image is still a capture.</summary>
public record FetchedAsset(string MediaType, byte[] Content);

/// <summary>Turns fetched HTML into the file a bookmark keeps: one self-contained
/// document that still reads when the site it came from is gone. Scripts and frames are
/// removed — a snapshot is a record, not a running program, and stored markup that could
/// execute is stored markup that will eventually execute as someone else. Stylesheets
/// and images are folded in (within a budget the caller's fetcher enforces), every
/// remaining reference is made absolute so it means the same thing from disk, and the
/// first line of the file says where and when it was captured, for whoever finds it with
/// no Gatherum running.</summary>
public static partial class PageSnapshot
{
    /// <summary>Rendered snapshot: the page's own title, and the bytes to keep.</summary>
    public record Result(string Title, byte[] Content);

    public static async Task<Result> BuildAsync(Uri url, byte[] html,
        Func<Uri, CancellationToken, Task<FetchedAsset?>> fetchAsset,
        DateTimeOffset capturedAt, CancellationToken ct = default)
    {
        var parser = new HtmlParser();
        using var source = new MemoryStream(html);
        var document = await parser.ParseDocumentAsync(source, ct);

        var baseUri = EffectiveBase(url, document);
        RemoveActiveContent(document);
        AbsolutizeReferences(document, baseUri);
        await InlineStylesheetsAsync(document, fetchAsset, ct);
        await InlineStyleElementsAsync(document, fetchAsset, ct);
        await InlineImagesAsync(document, fetchAsset, ct);
        DeclareUtf8(document);
        Stamp(document, url, capturedAt);

        var title = document.Title?.Trim() is { Length: > 0 } own
            ? own
            : url.Host + url.AbsolutePath.TrimEnd('/');
        return new Result(title, Encoding.UTF8.GetBytes(document.ToHtml()));
    }

    /// <summary>The base everything relative resolves against: the page's own
    /// <c>&lt;base&gt;</c> when it declares one, else the URL it was fetched from. The
    /// element itself is dropped — after absolutization it has nothing left to say, and
    /// left in place it would re-relativize a snapshot served from somewhere else.</summary>
    private static Uri EffectiveBase(Uri url, IDocument document)
    {
        var declared = document.QuerySelector("base[href]")?.GetAttribute("href");
        foreach (var baseElement in document.QuerySelectorAll("base").ToList())
            baseElement.Remove();
        return declared is not null && Uri.TryCreate(url, declared, out var resolved)
            ? resolved
            : url;
    }

    private static void RemoveActiveContent(IDocument document)
    {
        foreach (var element in document
            .QuerySelectorAll("script, iframe, frame, frameset, object, embed, applet")
            .ToList())
            element.Remove();

        // Resource hints point loaders at the live site, and a refresh would navigate
        // the reader away from the very thing they saved.
        foreach (var link in document.QuerySelectorAll("link[rel]").ToList())
        {
            var rel = link.GetAttribute("rel") ?? "";
            if (rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(r =>
                    r is "preload" or "modulepreload" or "prefetch" or "dns-prefetch"
                        or "preconnect"))
                link.Remove();
        }
        foreach (var meta in document.QuerySelectorAll("meta[http-equiv]").ToList())
        {
            if (string.Equals(meta.GetAttribute("http-equiv"), "refresh",
                    StringComparison.OrdinalIgnoreCase))
                meta.Remove();
        }

        foreach (var element in document.All.ToList())
        {
            foreach (var attribute in element.Attributes.ToList())
            {
                if (attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    element.RemoveAttribute(attribute.Name);
                else if (attribute.Name is "href" or "src" or "action"
                    && attribute.Value.TrimStart().StartsWith("javascript:",
                        StringComparison.OrdinalIgnoreCase))
                    element.RemoveAttribute(attribute.Name);
            }
        }
    }

    private static readonly string[] UrlAttributes =
        ["href", "src", "poster", "action", "cite", "data"];

    private static void AbsolutizeReferences(IDocument document, Uri baseUri)
    {
        foreach (var element in document.All)
        {
            foreach (var name in UrlAttributes)
            {
                if (element.GetAttribute(name) is { } value
                    && Absolutize(baseUri, value) is { } absolute)
                    element.SetAttribute(name, absolute);
            }
            if (element.GetAttribute("srcset") is { } srcset)
                element.SetAttribute("srcset", AbsolutizeSrcset(baseUri, srcset));
            if (element.GetAttribute("style") is { } style)
                element.SetAttribute("style", AbsolutizeCss(baseUri, style));
            // A capture is read all at once; lazy loading would leave its images
            // waiting for a scroll that already happened.
            element.RemoveAttribute("loading");
        }
        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = AbsolutizeCss(baseUri, style.TextContent);
    }

    private static async Task InlineStylesheetsAsync(IDocument document,
        Func<Uri, CancellationToken, Task<FetchedAsset?>> fetchAsset, CancellationToken ct)
    {
        foreach (var link in document.QuerySelectorAll("link[rel~='stylesheet'][href]").ToList())
        {
            if (!Uri.TryCreate(link.GetAttribute("href"), UriKind.Absolute, out var href)
                || href.Scheme is not ("http" or "https"))
                continue;
            var asset = await fetchAsset(href, ct);
            if (asset is null)
                continue;

            var style = document.CreateElement("style");
            if (link.GetAttribute("media") is { } media)
                style.SetAttribute("media", media);
            // The sheet's own relative references resolve against the sheet, not the page.
            style.TextContent = await InlineCssAssetsAsync(
                AbsolutizeCss(href, Encoding.UTF8.GetString(asset.Content)), fetchAsset, ct);
            link.Parent?.ReplaceChild(style, link);
        }
    }

    /// <summary>Styles the page carries in its own body — hand-written or, in a rendered
    /// capture, put there by the scripts that ran — get their fonts and background
    /// images folded in like a linked sheet's. Their references are already absolute:
    /// <see cref="AbsolutizeReferences"/> went first.</summary>
    private static async Task InlineStyleElementsAsync(IDocument document,
        Func<Uri, CancellationToken, Task<FetchedAsset?>> fetchAsset, CancellationToken ct)
    {
        foreach (var style in document.QuerySelectorAll("style"))
            style.TextContent = await InlineCssAssetsAsync(style.TextContent, fetchAsset, ct);
    }

    /// <summary>Folds what a stylesheet points at — fonts, background images — into
    /// data: URIs, so type and texture survive the site. Every reference is fetched at
    /// most once, and one that cannot be had stays a live URL, like a dead image.</summary>
    private static async Task<string> InlineCssAssetsAsync(string css,
        Func<Uri, CancellationToken, Task<FetchedAsset?>> fetchAsset, CancellationToken ct)
    {
        var inlined = new Dictionary<string, string>();
        foreach (var reference in CssUrl().Matches(css)
            .Select(match => match.Groups["url"].Value).Distinct())
        {
            if (!Uri.TryCreate(reference, UriKind.Absolute, out var target)
                || target.Scheme is not ("http" or "https"))
                continue;
            if (await fetchAsset(target, ct) is { } asset)
                inlined[reference] =
                    $"data:{asset.MediaType};base64,{Convert.ToBase64String(asset.Content)}";
        }
        if (inlined.Count == 0)
            return css;
        return CssUrl().Replace(css, match =>
            inlined.TryGetValue(match.Groups["url"].Value, out var data)
                ? $"url({match.Groups["q"].Value}{data}{match.Groups["q"].Value})"
                : match.Value);
    }

    private static async Task InlineImagesAsync(IDocument document,
        Func<Uri, CancellationToken, Task<FetchedAsset?>> fetchAsset, CancellationToken ct)
    {
        foreach (var image in document.QuerySelectorAll("img[src]").OfType<IHtmlImageElement>())
        {
            if (!Uri.TryCreate(image.GetAttribute("src"), UriKind.Absolute, out var src)
                || src.Scheme is not ("http" or "https"))
                continue;
            var asset = await fetchAsset(src, ct);
            if (asset is null || !asset.MediaType.StartsWith("image/", StringComparison.Ordinal))
                continue;
            image.SetAttribute("src",
                $"data:{asset.MediaType};base64,{Convert.ToBase64String(asset.Content)}");
            // The inlined bytes are the capture; a srcset would send the browser back to
            // the live site for a sharper copy that may no longer match.
            image.RemoveAttribute("srcset");
        }
    }

    /// <summary>The snapshot is re-serialized as UTF-8 whatever the page declared, so
    /// the declaration has to say so — a stale charset would garble every non-ASCII
    /// character in the file.</summary>
    private static void DeclareUtf8(IDocument document)
    {
        foreach (var meta in document.QuerySelectorAll("meta[charset]").ToList())
            meta.Remove();
        foreach (var meta in document.QuerySelectorAll("meta[http-equiv]").ToList())
        {
            if (string.Equals(meta.GetAttribute("http-equiv"), "content-type",
                    StringComparison.OrdinalIgnoreCase))
                meta.Remove();
        }
        if (document.Head is { } head)
        {
            var charset = document.CreateElement("meta");
            charset.SetAttribute("charset", "utf-8");
            head.InsertBefore(charset, head.FirstChild);
        }
    }

    private static void Stamp(IDocument document, Uri url, DateTimeOffset capturedAt)
    {
        var stamp = document.CreateComment(
            $" saved from {url.AbsoluteUri} by Gatherum on {capturedAt:yyyy-MM-dd HH:mm 'UTC'} ");
        document.InsertBefore(stamp, document.DocumentElement);
    }

    /// <summary>An absolute spelling for a reference, or null when it should stay as it
    /// is — already carrying a scheme (https:, data:, mailto:), a bare fragment, or
    /// beyond making sense of. "Has a scheme" is spelled out by hand because
    /// <see cref="Uri.TryCreate(string,UriKind,out Uri)"/> calls a root-relative path
    /// like <c>/about</c> an absolute file URI on Unix, which is exactly the reference
    /// this method exists to resolve.</summary>
    private static string? Absolutize(Uri baseUri, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || HasScheme(trimmed))
            return null;
        return Uri.TryCreate(baseUri, trimmed, out var absolute) ? absolute.AbsoluteUri : null;
    }

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

    private static string AbsolutizeSrcset(Uri baseUri, string srcset) =>
        string.Join(", ", srcset
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(candidate =>
            {
                var parts = candidate.Split([' ', '\t'], 2,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0)
                    return candidate;
                var reference = Absolutize(baseUri, parts[0]) ?? parts[0];
                return parts.Length > 1 ? $"{reference} {parts[1]}" : reference;
            }));

    private static string AbsolutizeCss(Uri baseUri, string css)
    {
        var rewritten = CssUrl().Replace(css, match =>
        {
            var quote = match.Groups["q"].Value;
            var reference = Absolutize(baseUri, match.Groups["url"].Value)
                ?? match.Groups["url"].Value;
            return $"url({quote}{reference}{quote})";
        });
        return CssImport().Replace(rewritten, match =>
        {
            var quote = match.Groups["q"].Value;
            var reference = Absolutize(baseUri, match.Groups["url"].Value)
                ?? match.Groups["url"].Value;
            return $"@import {quote}{reference}{quote}";
        });
    }

    [GeneratedRegex("""url\(\s*(?<q>['"]?)(?<url>[^'")]+)\k<q>\s*\)""", RegexOptions.IgnoreCase)]
    private static partial Regex CssUrl();

    [GeneratedRegex("""@import\s+(?<q>['"])(?<url>[^'"]+)\k<q>""", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();
}
