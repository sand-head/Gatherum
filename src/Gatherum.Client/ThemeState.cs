using Microsoft.JSInterop;

namespace Gatherum.Client;

/// <summary>Which color mode is in effect right now, for the components that paint
/// outside CSS (the slopedit canvas, and the stylesheet its HTML view bakes). Fed by
/// gatherum.js's watchTheme, which folds the explicit data-theme choice and the OS
/// preference into one boolean and reports every later change.</summary>
/// <param name="startDark">What to assume until JS answers. Only a prerender ever
/// needs it — there is no JS to ask, and a read view prerendered in the wrong mode is
/// a white article until the island goes interactive — so the server seeds it from
/// what the request said (<c>Gatherum.Web.Services.BrowserTheme</c>). Everywhere else
/// the watch answers before anything is painted.</param>
public sealed class ThemeState(IJSRuntime js, bool startDark = false) : IDisposable
{
    private DotNetObjectReference<ThemeState>? selfRef;
    private bool watching;

    public bool IsDark { get; private set; } = startDark;
    public event Action? Changed;

    /// <summary>Idempotent; the first interactive component that needs the theme
    /// starts the watch. Must not be called while prerendering — there is no JS yet.</summary>
    public async Task EnsureWatchingAsync()
    {
        if (watching)
            return;
        watching = true;
        var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/gatherum.js");
        selfRef = DotNetObjectReference.Create(this);
        var dark = await module.InvokeAsync<bool>("watchTheme", selfRef);
        OnThemeChanged(dark);
    }

    [JSInvokable]
    public void OnThemeChanged(bool isDark)
    {
        if (isDark == IsDark)
            return;
        IsDark = isDark;
        Changed?.Invoke();
    }

    public void Dispose() => selfRef?.Dispose();
}
