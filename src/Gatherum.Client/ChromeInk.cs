using SlopEdit.Core.Text;

namespace Gatherum.Client;

/// <summary>The colors the constructs a page assembles are painted in — an infobox's
/// card, a figure's frame, each callout kind's accent, a wiki link that points at no
/// page yet. Canvas paint, so out of CSS's reach: these are the app.css tokens restated
/// as <see cref="CellColor"/>s, the same job <see cref="EditorThemes"/> does for the
/// editor's own ink, and they are re-declared (not baked in at parse) whenever the mode
/// changes — see <see cref="DocumentChrome"/>.</summary>
/// <param name="CardFill">An aside's paper — <c>--surface-dim</c>, the tonal fill the
/// app gives every inset block (a code band is the same value).</param>
/// <param name="CardBorder">Its hairline — <c>--outline-dim</c>, the weight the content
/// sheet is drawn with: a tonal card wants an edge you can find, not one you read.</param>
/// <param name="Band">The tint behind an infobox's title — <c>--chip-bg</c>.</param>
/// <param name="OnBand">The title's own ink on that tint — <c>--on-chip</c>, so the
/// card's heading is the same object as the category chips at the foot of a page.</param>
/// <param name="DeadLink">A <c>[[wiki link]]</c> whose title names nothing — <c>--danger</c>.</param>
/// <param name="LockedLink">A link to a node this reader may not open — <c>--on-surface-dim</c>.
/// Grey rather than red, because nothing is wrong with it: the page is simply not
/// addressed to you.</param>
public sealed record ChromeInk(CellColor CardFill, CellColor CardBorder, CellColor Band,
    CellColor OnBand, CellColor DeadLink, CellColor LockedLink, CellColor Surface, bool IsDark)
{
    private static readonly ChromeInk LightInk = new(
        CardFill: CellColor.Rgb(0xf1, 0xf3, 0xf4),
        CardBorder: CellColor.Rgb(0xe9, 0xea, 0xee),
        Band: CellColor.Rgb(0xe8, 0xf0, 0xfe),
        OnBand: CellColor.Rgb(0x19, 0x67, 0xd2),
        DeadLink: CellColor.Rgb(0xd9, 0x30, 0x25),
        LockedLink: CellColor.Rgb(0x5f, 0x63, 0x68),
        Surface: CellColor.Rgb(0xff, 0xff, 0xff),
        IsDark: false);

    private static readonly ChromeInk DarkInk = new(
        CardFill: CellColor.Rgb(0x28, 0x29, 0x2c),
        CardBorder: CellColor.Rgb(0x33, 0x35, 0x37),
        Band: CellColor.Rgb(0x1f, 0x37, 0x60),
        OnBand: CellColor.Rgb(0xa8, 0xc7, 0xfa),
        DeadLink: CellColor.Rgb(0xf2, 0x8b, 0x82),
        LockedLink: CellColor.Rgb(0x9a, 0xa0, 0xa6),
        Surface: CellColor.Rgb(0x1e, 0x1f, 0x20),
        IsDark: true);

    public static ChromeInk For(bool isDark) => isDark ? DarkInk : LightInk;

    /// <summary>A callout kind's accent, and the wash of it that fills the card. The
    /// fill is mixed here rather than declared because a decoration's background is
    /// opaque — there is no alpha to tint with, so the tint is computed against the
    /// surface it will sit on.</summary>
    public (CellColor Fill, CellColor Border, CellColor Ink) Callout(string kind)
    {
        var accent = kind.ToLowerInvariant() switch
        {
            "tip" => IsDark ? CellColor.Rgb(0x81, 0xc9, 0x95) : CellColor.Rgb(0x13, 0x73, 0x33),
            "important" => IsDark ? CellColor.Rgb(0xc5, 0x8a, 0xf9) : CellColor.Rgb(0x84, 0x30, 0xce),
            "warning" => IsDark ? CellColor.Rgb(0xfd, 0xd6, 0x63) : CellColor.Rgb(0xb0, 0x60, 0x00),
            "caution" => IsDark ? CellColor.Rgb(0xf2, 0x8b, 0x82) : CellColor.Rgb(0xd9, 0x30, 0x25),
            _ => IsDark ? CellColor.Rgb(0x8a, 0xb4, 0xf8) : CellColor.Rgb(0x1a, 0x73, 0xe8),
        };
        return (Mix(accent, Surface, IsDark ? 0.18f : 0.08f), accent, accent);
    }

    private static CellColor Mix(CellColor accent, CellColor ground, float amount) =>
        CellColor.Rgb(
            Blend(accent.R, ground.R, amount),
            Blend(accent.G, ground.G, amount),
            Blend(accent.B, ground.B, amount));

    private static byte Blend(byte a, byte b, float t) => (byte)Math.Round(a * t + b * (1 - t));
}
