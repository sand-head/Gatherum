using System.Security.Claims;
using System.Text.Encodings.Web;
using Gatherum.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Gatherum.Web.Auth;

/// <summary>Authenticates "Authorization: Bearer gk_…" requests against stored API key
/// hashes. Anything else is left for the cookie scheme to handle.</summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        var keys = Context.RequestServices.GetRequiredService<ApiKeyService>();
        var key = await keys.ValidateAsync(token, Context.RequestAborted);
        if (key is null)
            return AuthenticateResult.Fail("Unknown or revoked API key.");

        var principal = new ClaimsPrincipal(key.User!.ToIdentity(SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
