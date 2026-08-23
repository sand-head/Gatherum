using Gatherum.Core;
using Gatherum.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Gatherum.Web.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app, OidcOptions oidc)
    {
        app.MapGet("/auth/login", async (HttpContext http, UserService users, string? returnUrl) =>
        {
            var target = SafeReturnUrl(returnUrl);
            if (oidc.IsConfigured)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = target },
                    [OpenIdConnectDefaults.AuthenticationScheme]);

            // No identity provider configured: sign in a local development user so the
            // app is usable straight from `dotnet run`. Never configure production this way.
            var user = await users.GetOrCreateAsync("dev", "dev@localhost", "Dev User", "dev");
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new System.Security.Claims.ClaimsPrincipal(
                    user.ToIdentity(CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.LocalRedirect(target);
        }).AllowAnonymous();

        app.MapPost("/auth/logout", (HttpContext http) => Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme]));
    }

    private static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url.StartsWith('/') && !url.StartsWith("//") ? url : "/";
}
