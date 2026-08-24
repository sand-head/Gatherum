namespace Gatherum.Web.Services;

/// <summary>What a request says the browser is painting — the one thing a prerender
/// needs and cannot ask for, because the explicit choice is a <c>data-theme</c>
/// attribute set in the browser and the fallback is an OS preference that never leaves
/// it. gatherum.js folds the two into a color and publishes it as a cookie for this;
/// a browser that has never been here has no cookie, and Chromium volunteers the OS
/// half as a client hint instead.
///
/// It matters because slopedit's HTML view bakes a theme's colors into the stylesheet
/// it emits rather than reaching for CSS variables, so an article prerendered in the
/// wrong mode is a white page until the island goes interactive.
/// </summary>
public static class BrowserTheme
{
    /// <summary>The cookie gatherum.js writes the resolved mode into. Spelled the same
    /// way on both sides, and nowhere else.</summary>
    public const string ModeCookie = "gatherum-mode";

    /// <summary>The hint asked for by the <c>Accept-CH</c> header the pipeline puts on
    /// every document response, and volunteered on the next request by browsers that
    /// support it. The OS preference only — a reader who overrode it has the cookie.</summary>
    public const string ClientHint = "Sec-CH-Prefers-Color-Scheme";

    private const string Dark = "dark";

    /// <summary>Dark, light, or null when the request said neither, which is not the
    /// same as light: the difference is a caller's to spend, and the callers that have
    /// no third answer fall back to light exactly as the app always did.</summary>
    public static bool? IsDark(HttpContext? context)
    {
        if (context is null)
            return null;
        var mode = context.Request.Cookies[ModeCookie];
        if (string.IsNullOrEmpty(mode))
            mode = context.Request.Headers[ClientHint].ToString();
        return string.IsNullOrEmpty(mode) ? null : mode == Dark;
    }
}
