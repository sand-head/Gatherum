using SkiaSharp;
using SlopEdit.Blazor.Rendering;
using SlopEdit.Core.Highlighting;
using SlopEdit.Core.Rich;
using SlopEdit.Core.Text;

namespace Gatherum.Client;

/// <summary>slopedit paints to canvas, out of CSS's reach, so the app.css color
/// tokens are restated here as SKColors — same values, same roles. Change a token
/// there, change it here.</summary>
public static class EditorThemes
{
    public static readonly EditorTheme Light = new()
    {
        Background = new SKColor(0xff, 0xff, 0xff),        // --surface
        Foreground = new SKColor(0x1f, 0x1f, 0x1f),        // --on-surface
        Caret = new SKColor(0x1f, 0x1f, 0x1f),
        CurrentLine = new SKColor(0xf1, 0xf3, 0xf4),       // --surface-dim
        Selection = new SKColor(0xd3, 0xe3, 0xfd),         // --selected
        GutterForeground = new SKColor(0x5f, 0x63, 0x68),  // --on-surface-dim
        ScrollbarTrack = SKColors.Transparent,
        ScrollbarThumb = new SKColor(0x5f, 0x63, 0x68, 0x80),
        // The app's serif on the article's own section titles — the same family
        // list app.css calls --font-serif, led by the face DocumentFonts ships so
        // canvas and browser resolve the same file. Body and code keep slopedit's
        // embedded defaults, which are already guaranteed on both renderers.
        HeadingFontFamily = DocumentFonts.HeadingFamilies,
    };

    public static readonly EditorTheme Dark = new()
    {
        Background = new SKColor(0x1e, 0x1f, 0x20),
        Foreground = new SKColor(0xe3, 0xe3, 0xe3),
        Caret = new SKColor(0xe3, 0xe3, 0xe3),
        CurrentLine = new SKColor(0x28, 0x29, 0x2c),
        Selection = new SKColor(0x00, 0x4a, 0x77),
        GutterForeground = new SKColor(0x9a, 0xa0, 0xa6),
        ScrollbarTrack = SKColors.Transparent,
        ScrollbarThumb = new SKColor(0x9a, 0xa0, 0xa6, 0x80),
        HeadingFontFamily = DocumentFonts.HeadingFamilies,
    };

    public static EditorTheme For(bool isDark) => isDark ? Dark : Light;

    /// <summary>The rich document's own ink: links in the app's link blue, dim list
    /// markers and rules, warm inline code (slopedit's dark default kept for dark).</summary>
    public static void ApplyInk(RichDocument doc, bool isDark)
    {
        doc.LinkColor = isDark ? CellColor.Rgb(0x8a, 0xb4, 0xf8) : CellColor.Rgb(0x1a, 0x73, 0xe8);
        doc.MarkerColor = isDark ? CellColor.Rgb(0x9a, 0xa0, 0xa6) : CellColor.Rgb(0x5f, 0x63, 0x68);
        doc.CodeColor = isDark ? CellColor.Rgb(0xce, 0x91, 0x78) : CellColor.Rgb(0xb0, 0x60, 0x00);
        doc.RuleColor = isDark ? CellColor.Rgb(0x44, 0x47, 0x46) : CellColor.Rgb(0xda, 0xdc, 0xe0);
    }

    /// <summary>Source-mode syntax colors. Dark keeps slopedit's default (VS dark);
    /// light restates the classic VS light palette, reusing each kind's default flags
    /// so behaviors like the link underline carry over.</summary>
    public static SyntaxTheme Syntax(bool isDark)
    {
        if (isDark)
            return SyntaxTheme.Default;

        var light = new SyntaxTheme();
        foreach (var (kind, color) in new (TokenKind, CellColor)[]
        {
            (TokenKind.Keyword, CellColor.Rgb(0x00, 0x00, 0xff)),
            (TokenKind.Type, CellColor.Rgb(0x26, 0x7f, 0x99)),
            (TokenKind.String, CellColor.Rgb(0xa3, 0x15, 0x15)),
            (TokenKind.Comment, CellColor.Rgb(0x00, 0x80, 0x00)),
            (TokenKind.Number, CellColor.Rgb(0x09, 0x86, 0x58)),
            (TokenKind.Preprocessor, CellColor.Rgb(0xaf, 0x00, 0xdb)),
            (TokenKind.Constant, CellColor.Rgb(0x00, 0x70, 0xc1)),
            (TokenKind.Operator, CellColor.Rgb(0x38, 0x38, 0x38)),
            (TokenKind.Function, CellColor.Rgb(0x79, 0x5e, 0x26)),
            (TokenKind.Tag, CellColor.Rgb(0x26, 0x7f, 0x99)),
            (TokenKind.Attribute, CellColor.Rgb(0x00, 0x10, 0x80)),
            (TokenKind.Link, CellColor.Rgb(0x1a, 0x73, 0xe8)),
            (TokenKind.ListMarker, CellColor.Rgb(0x1a, 0x73, 0xe8)),
            (TokenKind.Delimiter, CellColor.Rgb(0x6e, 0x76, 0x81)),
        })
        {
            var (_, flags) = SyntaxTheme.Default.Resolve(kind);
            light.Set(kind, color, flags);
        }
        return light;
    }
}
