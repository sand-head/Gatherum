using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Gatherum.Web.Services;

/// <summary>
/// The manual, compiled into the app. Gatherum speaks a Markdown dialect nothing else
/// knows — wiki links, asides, callouts — so the explanation of it has to travel with
/// the copy it explains: every install serves this at <c>/docs</c>, in HTML for a person
/// and as its own Markdown for whatever a person points at it.
///
/// The pages are embedded resources rather than files on disk, so there is no directory
/// a deployment can forget to copy, and they are rendered once here rather than per
/// request — a singleton holding ten strings.
/// </summary>
public sealed class DocsLibrary
{
    /// <summary>Where the manual lives, and the prefix every link in it is written
    /// against.</summary>
    public const string Root = "/docs";

    private const string ResourcePrefix = "Gatherum.Web.Docs.";
    private const string Extension = ".md";

    /// <summary>Reading order. A page not named here still ships — it sorts after these,
    /// alphabetically — so adding a file to <c>Docs/</c> is enough to publish it.</summary>
    private static readonly string[] Order =
    [
        "index", "markdown", "pages-and-files", "categories", "lists", "search",
        "sharing", "api", "mcp", "agents", "configuration",
    ];

    /// <summary>Prose, not pages: the docs are the app's own words, so raw HTML stays
    /// off and the constructs a manual actually needs — tables, task lists, anchored
    /// headings, GitHub's alerts, <c>:::</c> containers — stay on.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoIdentifiers()
        .UseAlertBlocks()
        .UseCustomContainers()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .DisableHtml()
        .Build();

    public DocsLibrary()
    {
        Pages = Load();
        Home = Pages.FirstOrDefault(p => p.Slug == "index") ?? Pages[0];
    }

    /// <summary>Every page, in reading order.</summary>
    public IReadOnlyList<DocPage> Pages { get; }

    /// <summary>What <c>/docs</c> itself shows.</summary>
    public DocPage Home { get; }

    public DocPage? Find(string? slug) => string.IsNullOrEmpty(slug)
        ? Home
        : Pages.FirstOrDefault(p => p.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>The whole manual as one Markdown file — the link to hand a model that
    /// would rather fetch once than crawl. Relative links are rewritten absolute,
    /// because the file is going to be read a long way from the app that served it.</summary>
    public string Manual(string origin) =>
        string.Join("\n\n---\n\n", Pages.Select(p => Absolute(p.Markdown, origin))
            .Prepend($"""
                <!-- Gatherum documentation, assembled from {origin}{Root}. -->

                # Gatherum documentation

                Every page of this manual, in reading order. Each is also served on its own
                at `{origin}{Root}/<page>.md`.
                """));

    /// <summary>The manual as an <see href="https://llmstxt.org">llms.txt</see> index:
    /// what is here and where to fetch it.</summary>
    public string LlmsTxt(string origin)
    {
        var lines = new List<string>
        {
            "# Gatherum",
            "",
            "> A self-hosted knowledge base where pages and files are the same kind of thing:",
            "> every item is a node in one tree, with categories, links, versions and search.",
            "> Pages are Markdown files written in a small dialect of Gatherum's own.",
            "",
            $"The whole manual as one file: {origin}{Root}/all.md",
            "",
            "## Documentation",
            "",
        };
        lines.AddRange(Pages.Select(p =>
            $"- [{p.Title}]({origin}{Root}/{p.Slug}{Extension}): {p.Summary}"));
        return string.Join('\n', lines) + "\n";
    }

    private static string Absolute(string markdown, string origin) =>
        markdown.Replace($"]({Root}/", $"]({origin}{Root}/", StringComparison.Ordinal);

    private static List<DocPage> Load()
    {
        var assembly = typeof(DocsLibrary).Assembly;
        var sources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                name.EndsWith(Extension, StringComparison.Ordinal))
            .ToDictionary(name => name[ResourcePrefix.Length..^Extension.Length]);
        if (sources.Count == 0)
        {
            // Only ever a build problem — the pages are embedded, so they cannot go
            // missing at run time — and a silently empty manual would be worse.
            throw new InvalidOperationException(
                $"No documentation was embedded under '{ResourcePrefix}'.");
        }

        var slugs = Order.Where(sources.ContainsKey)
            .Concat(sources.Keys.Except(Order).Order(StringComparer.Ordinal));
        return [.. slugs.Select(slug => Read(assembly, sources[slug], slug))];
    }

    private static DocPage Read(System.Reflection.Assembly assembly, string resource, string slug)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Documentation resource '{resource}' is unreadable.");
        using var reader = new StreamReader(stream);
        // Normalized, so what is served does not depend on how the repo was checked out.
        var markdown = reader.ReadToEnd().Replace("\r\n", "\n");

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var headings = document.Descendants<HeadingBlock>().ToList();
        var title = headings.FirstOrDefault(h => h.Level == 1) is { } h1 ? PlainText(h1) : slug;
        var sections = headings
            .Where(h => h.Level == 2 && h.GetAttributes().Id is { Length: > 0 })
            .Select(h => new DocSection(h.GetAttributes().Id!, PlainText(h)))
            .ToList();

        return new DocPage(slug, title, Summary(markdown), markdown,
            Markdig.Markdown.ToHtml(document, Pipeline), sections);
    }

    /// <summary>The first paragraph, as one line: what a page is, for the index and for
    /// the llms.txt entry.</summary>
    private static string Summary(string markdown)
    {
        var paragraph = new List<string>();
        foreach (var line in markdown.Split('\n').SkipWhile(l => !l.StartsWith("# ", StringComparison.Ordinal)).Skip(1))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                if (paragraph.Count > 0)
                    break;
                continue;
            }
            paragraph.Add(text);
        }
        return string.Join(' ', paragraph);
    }

    /// <summary>A heading as words. Code spans count — several headings here are mostly
    /// the syntax they are about, and a contents entry that drops them says nothing.</summary>
    private static string PlainText(LeafBlock block) => block.Inline is null
        ? ""
        : string.Concat(block.Inline.Descendants<Inline>().Select(inline => inline switch
        {
            LiteralInline literal => literal.ToString(),
            CodeInline code => code.Content,
            _ => "",
        }));
}

/// <summary>One page of the manual: its Markdown as written, its HTML as rendered, and
/// the second-level headings a reader can jump to.</summary>
public sealed record DocPage(string Slug, string Title, string Summary, string Markdown,
    string Html, IReadOnlyList<DocSection> Sections);

public sealed record DocSection(string Id, string Title);
