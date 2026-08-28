using System.Reflection;
using SlopEdit.Blazor.Rendering;

namespace Gatherum.Client;

/// <summary>
/// The serif the app's article headings wear, shipped as bytes. Skia resolves faces
/// through slopedit's <see cref="FontRegistry"/>, then installed fonts — and the WASM
/// island has no installed fonts at all — so the four Liberation Serif files ride in
/// this assembly as resources and are registered before any document lays out. The
/// same files sit in wwwroot/fonts, where app.css @font-faces them for
/// <c>DocumentHtmlView</c>'s read pass: both renderers measure the same glyphs, which
/// is the parity rule typefaces obey like everything else.
/// </summary>
public static class DocumentFonts
{
    /// <summary>The heading fallback list both themes name: the registered face
    /// first, so every machine resolves the same file; Wikipedia's own serif and
    /// app.css's --font-serif fallback behind it for anyone reading the family list
    /// without the registry (a browser that has not fetched the @font-face yet).</summary>
    public const string HeadingFamilies = "Liberation Serif, Linux Libertine, Georgia";

    private static bool registered;

    /// <summary>Register every shipped face, once per process — like a font
    /// directory. <see cref="FontRegistry"/> is process-wide, so the server
    /// registers at the first document it dresses and every circuit after costs
    /// nothing; a WASM visit does the same in its own process.</summary>
    public static void EnsureRegistered()
    {
        if (registered)
            return;
        registered = true;
        var assembly = typeof(DocumentFonts).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                continue;
            using var face = assembly.GetManifestResourceStream(name)!;
            FontRegistry.Register(face);
        }
    }
}
