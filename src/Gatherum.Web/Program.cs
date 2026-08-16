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

builder.Services.AddGatherum(builder.Configuration);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Gatherum.Web.Services.AppOperations>();
builder.Services.AddScoped<Gatherum.Web.Services.TreeState>();

builder.Services.AddSingleton<YDotNet.Server.Storage.IDocumentStorage,
    Gatherum.Infrastructure.Collaboration.YjsDocumentStorage>();
builder.Services.AddYDotNet().AddWebSockets(options =>
    options.OnAuthenticateAsync = async (http, document) =>
    {
        // The websocket handshake carries the auth cookie; anything less than a
        // signed-in user with visibility of the page is rejected before sync starts.
        if (http.User.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(document.DocumentName, out var nodeId))
            throw new UnauthorizedAccessException("Not allowed to join this document.");
        var nodes = http.RequestServices.GetRequiredService<Gatherum.Core.Services.NodeService>();
        var node = await nodes.GetVisibleAsync(http.User.GetUserId(), nodeId);
        if (node.Kind != Gatherum.Core.Domain.NodeKind.Page)
            throw new UnauthorizedAccessException("Only pages have live documents.");
    });
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
            var name = oidcIdentity.FindFirst("name")?.Value
                ?? oidcIdentity.FindFirst("preferred_username")?.Value
                ?? email;

            var users = context.HttpContext.RequestServices
                .GetRequiredService<Gatherum.Core.Services.UserService>();
            var user = await users.GetOrCreateAsync(subject, email, name);
            context.Principal = new System.Security.Claims.ClaimsPrincipal(
                user.ToIdentity(CookieAuthenticationDefaults.AuthenticationScheme));
        };
    });
}

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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.UseWebSockets();
app.Map("/collab", collab => collab.UseYDotnetWebSockets());

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
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
}

public partial class Program;
