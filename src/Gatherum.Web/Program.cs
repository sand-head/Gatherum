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
builder.Services.AddScoped<Gatherum.Client.IAppData, Gatherum.Web.Services.ServerAppData>();
builder.Services.AddScoped<Gatherum.Client.TreeState>();
builder.Services.AddScoped<Gatherum.Client.OutlineState>();
builder.Services.AddScoped<Gatherum.Client.ThemeState>();
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

            var users = context.HttpContext.RequestServices
                .GetRequiredService<Gatherum.Core.Services.UserService>();
            var user = await users.GetOrCreateAsync(subject, email, name, username);
            context.Principal = new System.Security.Claims.ClaimsPrincipal(
                user.ToIdentity(CookieAuthenticationDefaults.AuthenticationScheme));
        };
    });
}

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
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.StartsWithSegments("/mcp"),
    browser => browser.UseStatusCodePagesWithReExecute(
        "/not-found", createScopeForStatusCodePages: true));

// Kestrel's default body cap (~30 MB) is far below the upload ceiling the file
// endpoints promise; raise it for them before the endpoint reads the body.
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
app.MapAuthEndpoints(oidc);
app.MapGatherumApi();
app.MapMcp("/mcp").RequireAuthorization("Mcp");

app.MapGet("/healthz", async (GatherumDbContext db) =>
{
    await db.Database.ExecuteSqlRawAsync("SELECT 1");
    return Results.Ok(new { status = "healthy" });
}).AllowAnonymous();

await MigrateAsync(app);

if (!oidc.IsConfigured)
    app.Logger.LogWarning(
        "No OIDC authority configured (Gatherum__Oidc__Authority); using development auto-login.");

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
