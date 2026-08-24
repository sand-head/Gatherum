namespace Gatherum.Core.Domain;

public static class MediaTypes
{
    public const string Markdown = "text/markdown";
    public const string PlainText = "text/plain";
    public const string Binary = "application/octet-stream";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>What a directory is, when it is only a place to keep things. A folder
    /// somebody made in their file manager is a node too.</summary>
    public const string Directory = "inode/directory";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = Markdown,
        [".markdown"] = Markdown,
        [".txt"] = PlainText,
        [".json"] = "application/json",
        [".yaml"] = "application/x-yaml",
        [".yml"] = "application/x-yaml",
        [".xml"] = "application/xml",
        [".html"] = "text/html",
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".docx"] = Docx,
    };

    /// <summary>Extensions whose content is text even when the upload says otherwise —
    /// code, configs, and notes should be editable and searchable regardless of what a
    /// browser guessed at upload time.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".org", ".csv", ".tsv", ".log",
        ".json", ".yaml", ".yml", ".toml", ".ini", ".conf", ".container", ".env",
        ".xml", ".html", ".css", ".svg",
        ".cs", ".csproj", ".sln", ".slnx", ".razor", ".fs", ".vb",
        ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".rb", ".go", ".rs", ".java",
        ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".sh", ".bash", ".ps1", ".sql",
    };

    /// <summary>Resolves the stored media type for an upload: a meaningful declared
    /// type wins, known extensions refine the generic ones browsers fall back to.</summary>
    /// <summary>The extension a media type wants on disk, so a page Gatherum creates is
    /// a file anything else can open. The first extension mapped to the type wins, which
    /// keeps ".md" ahead of ".markdown".</summary>
    public static string ExtensionFor(string mediaType)
    {
        foreach (var (extension, type) in ByExtension)
        {
            if (type == mediaType)
                return extension;
        }
        return "";
    }

    public static string Resolve(string? declared, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var generic = string.IsNullOrWhiteSpace(declared) || declared is Binary or PlainText;
        if (!generic)
            return declared!;
        if (ByExtension.TryGetValue(extension, out var known))
            return known;
        return TextExtensions.Contains(extension) ? PlainText : Binary;
    }

    public static bool IsText(string mediaType, string fileName) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType is "application/json" or "application/xml" or "application/x-yaml" ||
        TextExtensions.Contains(Path.GetExtension(fileName));
}
