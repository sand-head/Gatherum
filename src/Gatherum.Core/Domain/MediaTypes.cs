namespace Gatherum.Core.Domain;

public static class MediaTypes
{
    public const string Markdown = "text/markdown";
    public const string PlainText = "text/plain";
    public const string Binary = "application/octet-stream";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Html = "text/html";
    public const string Epub = "application/epub+zip";

    /// <summary>Cartridge images, one media type per console, because what a ROM is
    /// for is which machine runs it — the player picks its core by this and nothing
    /// else.</summary>
    public const string NesRom = "application/x-nes-rom";
    public const string GameBoyRom = "application/x-gameboy-rom";
    public const string GameBoyColorRom = "application/x-gameboy-color-rom";
    public const string MasterSystemRom = "application/x-sms-rom";
    public const string GameGearRom = "application/x-gamegear-rom";
    public const string GameBoyAdvanceRom = "application/x-gba-rom";
    public const string SuperNintendoRom = "application/x-snes-rom";
    public const string GameCubeRom = "application/x-gamecube-rom";

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
        [".html"] = Html,
        [".htm"] = Html,
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".docx"] = Docx,
        [".epub"] = Epub,
        [".nes"] = NesRom,
        [".gb"] = GameBoyRom,
        [".gbc"] = GameBoyColorRom,
        [".sms"] = MasterSystemRom,
        [".gg"] = GameGearRom,
        [".gba"] = GameBoyAdvanceRom,
        [".sfc"] = SuperNintendoRom,
        [".smc"] = SuperNintendoRom,
        // `.iso` too, though it names every optical disc ever imaged: a GameCube disc
        // is what one is called far more often than `.gcm`, and a file that turns out
        // to be some other kind of disc gets a player that says so rather than a
        // download that never explains why.
        [".iso"] = GameCubeRom,
        [".gcm"] = GameCubeRom,
        [".rvz"] = GameCubeRom,
    };

    /// <summary>Extensions whose content is text even when the upload says otherwise —
    /// code, configs, and notes should be editable and searchable regardless of what a
    /// browser guessed at upload time.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".org", ".csv", ".tsv", ".log",
        ".json", ".yaml", ".yml", ".toml", ".ini", ".conf", ".container", ".env",
        ".xml", ".html", ".htm", ".css", ".svg",
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

    /// <summary>Whether this is a cartridge image the player can run.</summary>
    public static bool IsRom(string mediaType, string fileName) =>
        mediaType is NesRom or GameBoyRom or GameBoyColorRom
            or MasterSystemRom or GameGearRom or GameBoyAdvanceRom or SuperNintendoRom
            or GameCubeRom ||
        RomExtensions.Contains(Path.GetExtension(fileName));

    private static readonly HashSet<string> RomExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".nes", ".gb", ".gbc", ".sms", ".gg", ".gba", ".sfc", ".smc", ".iso", ".gcm", ".rvz" };

    public static bool IsText(string mediaType, string fileName) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
        mediaType is "application/json" or "application/xml" or "application/x-yaml" ||
        TextExtensions.Contains(Path.GetExtension(fileName));
}
