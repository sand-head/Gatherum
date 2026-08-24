using Gatherum.Web.Services;
using Microsoft.AspNetCore.Http;

namespace Gatherum.Tests;

/// <summary>What a request says the browser is painting. Pure reading — the half of the
/// prerender's theme that is not gatherum.js's.</summary>
public class BrowserThemeTests
{
    private static HttpContext Request(string? cookie = null, string? hint = null)
    {
        var context = new DefaultHttpContext();
        if (cookie is not null)
            context.Request.Headers.Cookie = $"{BrowserTheme.ModeCookie}={cookie}";
        if (hint is not null)
            context.Request.Headers["Sec-CH-Prefers-Color-Scheme"] = hint;
        return context;
    }

    [Theory]
    [InlineData("dark", true)]
    [InlineData("light", false)]
    public void The_cookie_gatherum_js_wrote_is_the_answer(string cookie, bool dark) =>
        Assert.Equal(dark, BrowserTheme.IsDark(Request(cookie)));

    [Fact]
    public void A_browser_that_has_never_been_here_can_still_say_what_the_OS_prefers() =>
        Assert.True(BrowserTheme.IsDark(Request(hint: "dark")));

    /// <summary>The hint is the OS preference; the cookie is what the reader chose,
    /// which may be the opposite of it. A choice beats a preference.</summary>
    [Fact]
    public void An_explicit_choice_outranks_the_client_hint() =>
        Assert.False(BrowserTheme.IsDark(Request(cookie: "light", hint: "dark")));

    /// <summary>Not light: a caller that cannot tell the difference would paint every
    /// first visit white, which is the whole thing this exists to stop.</summary>
    [Fact]
    public void Silence_is_silence()
    {
        Assert.Null(BrowserTheme.IsDark(Request()));
        Assert.Null(BrowserTheme.IsDark(null));
    }
}
