using System.Text;
using Gatherum.Core.Abstractions;

namespace Gatherum.Infrastructure.Extraction;

/// <summary>Text, markdown, code, and config files are their own search text.</summary>
public class PlainTextExtractor : ITextExtractor
{
    private const int MaxBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".org", ".csv", ".tsv", ".log",
        ".json", ".yaml", ".yml", ".toml", ".ini", ".conf", ".container", ".env",
        ".xml", ".html", ".css", ".svg",
        ".cs", ".csproj", ".sln", ".razor", ".fs", ".vb",
        ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".rb", ".go", ".rs", ".java",
        ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".sh", ".bash", ".ps1", ".sql",
    };

    public bool CanExtract(string mediaType, string fileName) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType is "application/json" or "application/xml" or "application/x-yaml" ||
        TextExtensions.Contains(Path.GetExtension(fileName));

    public async Task<string> ExtractAsync(Stream content, string mediaType, string fileName,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxBytes];
        var read = await reader.ReadBlockAsync(buffer, cancellationToken);
        return new string(buffer, 0, read);
    }
}
