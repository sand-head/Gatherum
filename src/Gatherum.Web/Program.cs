using Microsoft.AspNetCore.DataProtection;
using Gatherum.Core;
using Gatherum.Core.Data;
using Gatherum.Infrastructure;
using Gatherum.Web.Api;
using Gatherum.Web.Auth;
using Gatherum.Web.Components;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
    builder.Logging.ClearProviders().AddJsonConsole();

builder.Services.AddGatherum(builder.Configuration);
builder.Services.AddRazorComponents()
    // slopedit's editor interop can exceed Blazor's 32 KB default client→server
    // message cap once a document gets tall; a tight cap kills the circuit silently.
    .AddInteractiveServerComponents(options => { })
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 2 * 1024 * 1024)
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Gatherum.Web.Services.AppOperations>();
builder.Services.AddSingleton<Gatherum.Web.Services.PresenceTracker>();
builder.Services.AddSingleton<Gatherum.Web.Services.PlaySessions>();
builder.Services.AddSingleton<Gatherum.Web.Services.UploadStaging>();
builder.Services.AddSingleton<Gatherum.Web.Services.DocsLibrary>();
builder.Services.AddScoped<Gatherum.Client.IAppData, Gatherum.Web.Services.ServerAppData>();
builder.Services.AddScoped<Gatherum.Client.TreeState>();
builder.Services.AddScoped<Gatherum.Client.OutlineState>();
// Seeded from the request rather than left to guess: a prerender has no JS to ask, and
// guessing wrong is visible (see BrowserTheme). Once interactive there is no HttpContext
// and no need for one — the watch answers before anything is painted.
builder.Services.AddScoped<Gatherum.Client.ThemeState>(services => new Gatherum.Client.ThemeState(
    services.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(),
    Gatherum.Web.Services.BrowserTheme.IsDark(
        services.GetRequiredService<IHttpContextAccessor>().HttpContext) ?? false));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
    options.MultipartBodyLengthLimit = Gatherum.Client.IAppData.MaxUploadBytes);
builder.Services.AddMcpServer(options => options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
    {
        Name = "gatherum",
        Title = "Gatherum",
        Version = "1.0",
    })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<Gatherum.Web.Mcp.GatherumMcpTools>();

var oidc = builder.Configuration
    .GetSection($"{GatherumOptions.Section}:{nameof(GatherumOptions.Oidc)}")
    .Get<OidcOptions>() ?? new OidcOptions();

var authentication = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            // API and MCP clients want a status code, not a login page.
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/mcp"))
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else
                context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
        ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

if (oidc.IsConfigured)
{
    authentication.AddOpenIdConnect(options =>
    {
        options.Authority = oidc.Authority;
        options.ClientId = oidc.ClientId;
        options.ClientSecret = oidc.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
        options.Scope.Clear();
        foreach (var scope in oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(scope);
        if (oidc.RequestOfflineAccess && !options.Scope.Contains(OpenIdConnectScope.OfflineAccess))
            options.Scope.Add(OpenIdConnectScope.OfflineAccess);

        options.Events.OnRedirectToIdentityProvider = context =>
        {
            // The identity provider matches this string exactly, and when it does not the
            // error it returns names neither the value it got nor the ones it holds. Worse,
            // with Pushed Authorization Requests — which the handler uses whenever the
            // provider advertises the endpoint — the request never touches the browser, so
            // there is nowhere else to read it from. One line here turns "invalid_request"
            // into an answer.
            context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Gatherum.Auth")
                .LogInformation(
                    "Authorization request to {Authority} with redirect_uri {RedirectUri}. " +
                    "The provider must have this registered exactly, scheme included.",
                    context.ProtocolMessage.IssuerAddress,
                    context.ProtocolMessage.RedirectUri);
            return Task.CompletedTask;
        };

        options.Events.OnTicketReceived = async context =>
        {
            var oidcIdentity = context.Principal!;
            var subject = oidcIdentity.FindFirst("sub")?.Value
                ?? throw new InvalidOperationException("The identity token has no 'sub' claim.");
            var email = oidcIdentity.FindFirst("email")?.Value ?? "";
            // The username is what their directory gets named after, so it is read on its
            // own rather than as a fallback for a display name. Authelia sends the login
            // itself here; falling back to the subject keeps a provider that sends no
            // preferred_username working, if less legibly.
            var username = oidcIdentity.FindFirst("preferred_username")?.Value ?? subject;
            var name = oidcIdentity.FindFirst("name")?.Value ?? username;

            var groups = OidcGroups.From(oidcIdentity, oidc.GroupsClaim);
            if (oidc.RequiredGroup.Length > 0 && !OidcGroups.IsMember(groups, oidc.RequiredGroup))
            {
                // Refused before GetOrCreateAsync on purpose: somebody the provider
                // authenticated but this instance does not admit gets no user row and no
                // root directory out of the attempt.
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Gatherum.Auth")
                    .LogWarning(
                        "Sign-in refused: {Subject} is not in {RequiredGroup}. The token carried " +
                        "{GroupCount} group claim(s) in '{GroupsClaim}'; none at all usually means " +
                        "the scope that sends them is missing from Gatherum__Oidc__Scopes.",
                        subject, oidc.RequiredGroup, groups.Count, oidc.GroupsClaim);
                context.HandleResponse();
                context.Response.Redirect("/auth/denied");
                return;
            }

            var users = context.HttpContext.RequestServices
                .GetRequiredService<Gatherum.Core.Services.UserService>();
            var user = await users.GetOrCreateAsync(subject, email, name, username,
                oidc.AdminGroup.Length > 0 ? OidcGroups.IsMember(groups, oidc.AdminGroup) : null);
            context.Principal = new System.Security.Claims.ClaimsPrincipal(
                user.ToIdentity(CookieAuthenticationDefaults.AuthenticationScheme));
        };
    });
}

// Sign-in cookies are protected by keys that have to outlive the container. ASP.NET
// keeps them under the runtime user's home directory by default, which is inside the
// image on a good day and — when the container runs as a uid the image has never heard
// of, as TrueNAS's 568 is — unwritable, leaving keys that die with the process and sign
// everyone out on every restart. The database has none of those problems, and is already
// where the other two things a rebuild cannot recover live.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<GatherumDbContext>()
    // Pinned so the keys stay readable across renames of the app or its directory.
    .SetApplicationName("Gatherum");

builder.Services.AddAnonymousRateLimits();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder(
            CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
    // Browser-rendered file previews send cookies; scripts send API keys. Both are fine
    // for /api. MCP clients always hold an API key, so /mcp accepts nothing else.
    options.AddPolicy("Api", policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName,
            CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());
    options.AddPolicy("Mcp", policy => policy
        .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser());
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Ask for the reader's OS color preference, so the very first visit — the one with no
// cookie for gatherum.js to have written yet — still prerenders the article in the mode
// the browser is about to paint. Critical-CH is what makes the answer arrive on *this*
// navigation rather than the next one: a browser that has not been asked before retries
// the request once with the hint attached. Only Chromium answers at all; everywhere else
// the cookie covers every load after the first. Document responses only — an asset is not
// prerendered and has nothing to learn.
app.Use((context, next) =>
{
    if (context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
    {
        context.Response.Headers["Accept-CH"] = Gatherum.Web.Services.BrowserTheme.ClientHint;
        context.Response.Headers["Critical-CH"] = Gatherum.Web.Services.BrowserTheme.ClientHint;
        context.Response.Headers.Vary = Gatherum.Web.Services.BrowserTheme.ClientHint;
    }
    return next(context);
});

app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.StartsWithSegments("/mcp"),
    browser => browser.UseStatusCodePagesWithReExecute(
        "/not-found", createScopeForStatusCodePages: true));

// Kestrel's default body cap (~30 MB) is far below the upload ceiling the file
// endpoints promise; raise it for them before the endpoint reads the body. The chunked
// path under /api/uploads stays under the default by construction — that is its point.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/files"))
    {
        var sizeFeature = context.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = Gatherum.Client.IAppData.MaxUploadBytes;
    }
    return next(context);
});

// The one socket in the app: two people playing the same cartridge exchange their
// buttons over it. Before authentication, because the upgrade is a request like any
// other and the endpoint behind it still asks who is calling.
app.UseWebSockets();

app.UseAuthentication();
app.UseAuthorization();
// After authorization on purpose. The budget is only for callers with no session, and
// an API key is verified by the endpoint's own scheme rather than by UseAuthentication —
// so before this point a perfectly good key still looks like the internet, and the two
// people who own the instance would be metered against a bucket shared with it.
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Gatherum.Client.NodeEditor).Assembly);
app.MapAuthEndpoints(oidc, app.Environment.IsDevelopment());
app.MapGatherumApi();
app.MapGatherumDocs();
app.MapMcp("/mcp").RequireAuthorization("Mcp");

app.MapGet("/healthz", async (GatherumDbContext db) =>
{
    await db.Database.ExecuteSqlRawAsync("SELECT 1");
    return Results.Ok(new { status = "healthy" });
}).AllowAnonymous();

await MigrateAsync(app);

if (!oidc.IsConfigured)
{
    // Auth is OIDC-only, so the development auto-login is the one local account that
    // exists — and it signs anybody in without asking them anything. Outside
    // Development that is not a warning, it is an open door, and the app refuses to be
    // one. Loud at startup beats a line in a log nobody reads until afterward.
    if (!app.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "No identity provider is configured (Gatherum__Oidc__Authority, __ClientId, " +
            "__ClientSecret), and the development auto-login signs in anyone who asks. " +
            "Configure OIDC, or set ASPNETCORE_ENVIRONMENT=Development if this really is " +
            "a machine only you can reach.");
    }
    app.Logger.LogWarning(
        "No OIDC authority configured (Gatherum__Oidc__Authority); using development " +
        "auto-login. Anyone who can reach this app can sign in as the development user.");
}

app.Run();

static async Task MigrateAsync(WebApplication app)
{
    var options = app.Services.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<GatherumOptions>>().Value;
    if (!options.Database.Migrate)
        return;
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GatherumDbContext>();
    await db.Database.MigrateAsync();

    // The vector column's width is a property of whichever model will fill it, not of
    // the migration, so it is settled here — and only when something is going to.
    if (GatherumServiceCollectionExtensions.EmbeddingEnabled(app.Configuration))
        await Gatherum.Infrastructure.Data.EmbeddingSchema.EnsureAsync(
            db, options.Embedding.Dimensions, app.Logger);

    // Make the index agree with the directories. This is startup reconciliation and
    // disaster recovery at once: an empty database simply reports everything as added.
    if (options.Storage.ReindexOnStartup)
    {
        var report = await scope.ServiceProvider
            .GetRequiredService<Gatherum.Core.Services.Reindexer>().RunAsync();
        app.Logger.LogInformation(
            "Storage reconciled: {Added} added, {Updated} updated, {Moved} moved, {Removed} removed.",
            report.Added, report.Updated, report.Moved, report.Removed);
    }
}

public partial class Program;
