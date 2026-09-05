using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Bootstrap;
using FullWorth.Web.Modules.Landing;
using FullWorth.Web.Modules.Passkeys;
using FullWorth.Web.Modules.Pin;
using FullWorth.Web.Modules.Recovery;
using FullWorth.Web.Modules.Sessions;
using FullWorth.Web.Security;
using FullWorth.Web.Security.Antiforgery;
using FullWorth.Web.Security.BackendContext;
using FullWorth.Web.Security.Headers;
using FullWorth.Web.Security.RateLimiting;
using FullWorth.Web.Validation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using FinanceSessionOptions = FullWorth.Web.Modules.Sessions.SessionOptions;

const string SessionInvalidItem = "Finance.SessionInvalid";

var builder = WebApplication.CreateBuilder(args);

// P0.3: allow secrets to arrive as Docker secret files (evaluated at build time, before config reads).
FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);

var configuredAuth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var bulkReceiptImportMaxRequestBytes = Math.Clamp(
    builder.Configuration.GetValue<long?>("ReceiptImports:MaxUploadBytes") ?? 512L * 1024 * 1024,
    1L,
    1024L * 1024 * 1024);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    foreach (var configuredProxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(configuredProxy, out var address))
            options.KnownProxies.Add(address);
    }

    // Docker bridge addresses are dynamic, so deployments can trust the bridge CIDR instead of
    // pinning one ephemeral gateway address. Keep this explicit: forwarded headers from unknown
    // public networks are still rejected by ASP.NET Core.
    foreach (var configuredNetwork in builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
    {
        if (System.Net.IPNetwork.TryParse(configuredNetwork, out var network))
            options.KnownIPNetworks.Add(network);
    }
});

builder.Services.AddDbContext<AuthDbContext>((services, options) =>
{
    var authDatabase = services.GetRequiredService<IConfiguration>().GetConnectionString("AuthDatabase");
    if (string.IsNullOrWhiteSpace(authDatabase))
        throw new InvalidOperationException("ConnectionStrings:AuthDatabase is required.");

    options.UseNpgsql(authDatabase, npgsql =>
        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "auth"));
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddIdentityCore<AuthUser>(configuredAuth.Apply)
    .AddSignInManager()
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddDefaultTokenProviders();

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.Configure<FinanceSessionOptions>(builder.Configuration.GetSection("Sessions"));
builder.Services.Configure<RecoveryOptions>(builder.Configuration.GetSection("Recovery"));
builder.Services.AddSingleton(services => BackendContextOptions.Load(
    services.GetRequiredService<IConfiguration>(),
    services.GetRequiredService<IHostEnvironment>()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddTransient<BackendUserContextHandler>();
builder.Services.AddTransient<BankingUserContextHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AuthSessionCoordinator>();
builder.Services.AddScoped<InviteClaimService>();
builder.Services.Configure<InstallationOptions>(builder.Configuration.GetSection(InstallationOptions.SectionName));
builder.Services.AddScoped<InstallationStateService>();
builder.Services.AddSingleton<ILandingPageProvider, DefaultLandingPageProvider>();
builder.Services.AddScoped<ISessionPersistence, AuthSessionPersistence>();
builder.Services.AddScoped<SessionStore>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<IRecoveryCodeStore, AuthRecoveryCodeStore>();
builder.Services.AddScoped<IRecoveryUserValidator, AuthRecoveryUserValidator>();
builder.Services.AddScoped<RecoveryService>();
builder.Services.AddPasskeys(builder.Configuration);
builder.Services.AddScoped<PasskeyChallengeCleanup>();
builder.Services.AddScoped<PinService>();
builder.Services.AddFullWorthAntiforgery(builder.Environment.IsProduction());
builder.Services.AddFinanceRateLimiting(builder.Configuration);
builder.Services.AddFinanceSecurityHeaders();

// Persist the Data Protection key ring so auth cookies and antiforgery tokens survive restarts
// and are shared across instances. Without this they are ephemeral, breaking long-lived sessions
// and causing spurious CSRF failures on restart. Configure DataProtection:KeyPath to a durable
// (backed-up) directory in production; when unset the framework default (ephemeral) is used.
var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath))
{
    Directory.CreateDirectory(dataProtectionKeyPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
        .SetApplicationName("FullWorth.Web");
}

builder.Services.ConfigureApplicationCookie(cookie =>
{
    var configuredSessions = builder.Configuration.GetSection("Sessions").Get<FinanceSessionOptions>() ?? new FinanceSessionOptions();
    configuredSessions.Validate();
    SessionCookiePolicy.Apply(cookie, configuredSessions, builder.Environment.IsProduction());
    cookie.Events.OnValidatePrincipal = ValidateFinancePrincipalAsync;
    cookie.Events.OnRedirectToLogin = context => RedirectToLoginAsync(context, SessionInvalidItem);
    cookie.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

// The proxy clients must NOT follow redirects themselves: a 302 from a service (e.g. the Enable
// Banking callback answering "Location: /?bankConnected=…") has to reach the BROWSER, which resolves
// it against the public origin. With the default auto-follow the handler would chase "/" on the
// internal service (a 404) and the user would never see the redirect.
builder.Services.AddHttpClient("backend", client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:BackendUrl"] ?? "http://fullworth-backend:8080").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(5);
}).AddHttpMessageHandler<BackendUserContextHandler>()
  .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
var bankingBaseAddress = new Uri((builder.Configuration["Services:BankingUrl"] ?? "http://fullworth-banking:8080").TrimEnd('/') + "/");
builder.Services.AddHttpClient("banking", client =>
{
    client.BaseAddress = bankingBaseAddress;
    client.Timeout = TimeSpan.FromMinutes(5);
})
    // Outer: attach the trusted user + requested space from the session (backend re-verifies owner).
    .AddHttpMessageHandler<BankingUserContextHandler>()
    // Inner: the banking API key is attached ONLY here, after the handler's own same-origin check.
    .AddHttpMessageHandler(() => new ServiceProxyGuardHandler(bankingBaseAddress, "X-FullWorth-Banking-Key", builder.Configuration["Services:BankingApiKey"]))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
// Internal-key-only client (no user context) used solely for first-run admin bootstrap.
var bootstrapBackendBaseAddress = new Uri((builder.Configuration["Services:BackendUrl"] ?? "http://fullworth-backend:8080").TrimEnd('/') + "/");
builder.Services.AddHttpClient(FirstRunBootstrapper.BackendClientName, client =>
{
    client.BaseAddress = bootstrapBackendBaseAddress;
    client.Timeout = TimeSpan.FromSeconds(30);
})
    .AddHttpMessageHandler(() => new ServiceProxyGuardHandler(bootstrapBackendBaseAddress))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

var app = builder.Build();
_ = app.Services.GetRequiredService<BackendContextOptions>();

// P0.3 fail-closed + P1.2a host pinning, validated against the fully-merged configuration (Production only).
FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "ConnectionStrings:AuthDatabase", FullWorth.Shared.SecretBootstrap.SecretKind.ConnectionString);
FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Services:BankingApiKey");
if (app.Environment.IsProduction())
{
    var allowedHosts = app.Configuration["AllowedHosts"];
    if (string.IsNullOrWhiteSpace(allowedHosts) || allowedHosts.Split(';').Any(h => h.Trim() == "*"))
        throw new InvalidOperationException("AllowedHosts must be set to the production hostname(s) (not '*') before exposing FullWorth.Web.");
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await authDb.Database.MigrateAsync();
    await FirstRunBootstrapper.TryRunAsync(scope.ServiceProvider, app.Logger, CancellationToken.None);
}

// First in the pipeline: shape any unhandled exception as problem+json without leaking internals.
app.UseExceptionHandler();

app.UseForwardedHeaders();
app.UseFinanceSecurityHeaders();
if (app.Environment.IsProduction())
{
    app.UseFinanceProductionHsts(app.Environment);
    app.UseHttpsRedirection();
}
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();

app.Use(async (context, next) =>
{
    if ((context.Request.Path.Equals("/index.html") || context.Request.Path.Equals("/passkeys/index.html"))
        && context.User.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync(IdentityConstants.ApplicationScheme);
        return;
    }

    await next();
});

// Serve static assets with revalidation ("no-cache" = cache, but always check the ETag first). The
// auth/app shells are dynamic and always fresh, so without this a browser could keep an old cached
// CSS/JS after an update; ETag revalidation returns 304 when unchanged and fresh bytes after a deploy.
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
app.UseAuthorization();
app.UseFullWorthAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "fullworth-web" })).AllowAnonymous();
app.MapGet("/api/app-version", (IConfiguration cfg) =>
    Results.Ok(new { version = string.IsNullOrWhiteSpace(cfg["AppVersion"]) ? "dev" : cfg["AppVersion"]!.Trim() }))
    .AllowAnonymous();
app.MapGet("/appsettings.json", () => Results.NotFound()).AllowAnonymous();
app.MapFullWorthAntiforgeryTokenEndpoint();

var authShellPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "auth", "index.html");
// Asset cache-busting version. Production: the stable build version (cache-friendly, busts per release).
// Development: a fresh GUID per process start so every restart guarantees fresh assets — never a stale
// cached CSS/JS, never a manual hard-reload while iterating.
var assetVersion = string.IsNullOrWhiteSpace(app.Configuration["AppVersion"]) ? "dev" : app.Configuration["AppVersion"]!.Trim();
if (app.Environment.IsDevelopment())
    assetVersion = $"{assetVersion}-{Guid.NewGuid():N}";
foreach (var route in new[]
{
    "/auth",
    "/auth/login",
    "/auth/forgot-password",
    "/auth/reset-password",
    "/auth/recovery-code",
    "/auth/recovery-codes",
    "/auth/claim"
})
{
    app.MapGet(route, async (HttpContext context, CancellationToken ct) =>
    {
        // Version the linked CSS/JS so a browser always fetches the assets matching this build
        // instead of a heuristically-cached older copy (the shell HTML itself is always served fresh).
        var html = (await File.ReadAllTextAsync(authShellPath, ct))
            .Replace("/auth/auth.css", $"/auth/auth.css?v={assetVersion}")
            .Replace("/auth/auth.js", $"/auth/auth.js?v={assetVersion}");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, ct);
    }).AllowAnonymous();
}

var passkeyShellPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "passkeys", "index.html");
app.MapGet("/settings/security/passkeys", async (HttpContext context, CancellationToken ct) =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(passkeyShellPath, ct);
}).RequireAuthorization();

app.MapLandingEndpoints();
app.MapAuthEndpoints();
app.MapSessionEndpoints();
app.MapRecoveryEndpoints();
app.MapPasskeyEndpoints();
app.MapPinEndpoints();
FullWorth.Web.Modules.Import.FinanzguruImportPageEndpoints.MapFinanzguruImportPageEndpoints(app);
FullWorth.Web.Modules.Purchases.ShareReceiptEndpoints.MapShareReceiptEndpoints(app);

// The BFF proxy accepts ONLY relative paths under an explicit allowlist. The final URI is built
// server-side from the configured BaseAddress and verified for scheme/host/port/userinfo/fragment
// equality BEFORE any request object exists — a rejected path produces zero outbound traffic and
// the internal keys are attached later (inside the guarded handlers), never before validation.
string[] backendPathAllowlist = ["/api/"];
string[] bankingPathAllowlist = ["/api/banking/"];

// Large multipart bodies are accepted only on this exact authenticated endpoint. It also gets the
// stricter receipt-upload rate limiter instead of the generic browser API budget.
app.MapPost("/bff/backend/api/purchases/receipt-imports/upload", async (HttpContext context, IHttpClientFactory factory, CancellationToken ct) =>
{
    var maxBodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (maxBodySize is { IsReadOnly: false })
        maxBodySize.MaxRequestBodySize = bulkReceiptImportMaxRequestBytes;

    const string path = "api/purchases/receipt-imports/upload";
    var client = factory.CreateClient("backend");
    if (!ProxyTargetValidator.TryBuildTarget(client.BaseAddress!, path, context.Request.QueryString.Value ?? string.Empty, backendPathAllowlist, out var target))
        return Results.BadRequest();
    await ProxyAsync(context, client, target!, ct);
    return Results.Empty;
})
    .RequireAuthorization()
    .RequireRateLimiting(RateLimitPolicies.ReceiptUpload);

app.MapMethods("/bff/backend/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], async (HttpContext context, string path, IHttpClientFactory factory, CancellationToken ct) =>
{
    var normalizedPath = path.TrimStart('/');
    // The internal-key-only bootstrap seam (first-admin, accept-invite) runs with NO user context and must
    // never be reachable from an authenticated browser session, which would otherwise get the internal key
    // attached by the backend client. The legitimate claim flow calls it server-side, not through the BFF.
    if (normalizedPath.StartsWith("api/bootstrap", StringComparison.OrdinalIgnoreCase))
        return Results.NotFound();
    var client = factory.CreateClient("backend");
    if (!ProxyTargetValidator.TryBuildTarget(client.BaseAddress!, path, context.Request.QueryString.Value ?? string.Empty, backendPathAllowlist, out var target))
        return Results.BadRequest();
    await ProxyAsync(context, client, target!, ct);
    return Results.Empty;
})
    .RequireAuthorization()
    .RequireRateLimiting(RateLimitPolicies.BrowserApi);

app.MapMethods("/bff/banking/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], async (HttpContext context, string path, IHttpClientFactory factory, CancellationToken ct) =>
{
    var client = factory.CreateClient("banking");
    if (!ProxyTargetValidator.TryBuildTarget(client.BaseAddress!, path, context.Request.QueryString.Value ?? string.Empty, bankingPathAllowlist, out var target))
        return Results.BadRequest();
    await ProxyAsync(context, client, target!, ct);
    return Results.Empty;
})
    .RequireAuthorization()
    .RequireRateLimiting(RateLimitPolicies.BrowserApi);

app.MapGet("/connect/enable-banking/callback", async (HttpContext context, IHttpClientFactory factory, CancellationToken ct) =>
{
    var client = factory.CreateClient("banking");
    if (!ProxyTargetValidator.TryBuildTarget(client.BaseAddress!, "connect/enable-banking/callback", context.Request.QueryString.Value ?? string.Empty, ["/connect/enable-banking/callback"], out var target))
        return Results.BadRequest();
    await ProxyAsync(context, client, target!, ct);
    return Results.Empty;
})
    .AllowAnonymous();

app.MapFallbackToFile("index.html").RequireAuthorization();
app.Run();

static async Task ValidateFinancePrincipalAsync(CookieValidatePrincipalContext context)
{
    var principal = context.Principal;
    if (principal?.Identity?.IsAuthenticated != true ||
        !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var authUserId) ||
        !SessionClaims.TryGetSessionId(principal, out var sessionId))
    {
        await RejectPrincipalAsync(context);
        return;
    }

    var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<AuthUser>>();
    var user = await users.FindByIdAsync(authUserId.ToString());
    // Not gated by IsLockedOutAsync: a transient password lockout must not terminate an already
    // authenticated session (that would turn brute-force lockout into a session DoS). IsDisabled
    // (an explicit admin action) still revokes access.
    if (user is null || user.IsDisabled)
    {
        await RejectPrincipalAsync(context);
        return;
    }

    var securityStamp = await users.GetSecurityStampAsync(user);
    var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
    var validation = await sessions.ValidateSessionAsync(
        sessionId,
        authUserId,
        new SessionUserSecurityState(true, securityStamp),
        context.HttpContext.RequestAborted);

    if (!validation.IsValid)
        await RejectPrincipalAsync(context);
}

static async Task RejectPrincipalAsync(CookieValidatePrincipalContext context)
{
    context.HttpContext.Items[SessionInvalidItem] = true;
    context.RejectPrincipal();
    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
}

static Task RedirectToLoginAsync(RedirectContext<CookieAuthenticationOptions> context, string invalidItem)
{
    if (IsApiRequest(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
    var query = $"returnUrl={Uri.EscapeDataString(returnUrl)}";
    if (context.HttpContext.Items.ContainsKey(invalidItem))
        query += "&status=session-expired";

    context.Response.Redirect($"/auth/login?{query}");
    return Task.CompletedTask;
}

static bool IsApiRequest(PathString path) =>
    path.StartsWithSegments("/bff") ||
    path.StartsWithSegments("/auth/sessions") ||
    path.StartsWithSegments("/auth/recovery-codes") ||
    path.StartsWithSegments("/auth/passkeys") ||
    path.StartsWithSegments("/auth/pin") ||
    path.StartsWithSegments("/auth/logout") ||
    path.StartsWithSegments("/auth/change-password");

// The target URI has been validated by ProxyTargetValidator before this is called; service keys are
// attached exclusively inside the outbound handlers (after their own second origin check).
static async Task ProxyAsync(HttpContext context, HttpClient client, Uri target, CancellationToken ct)
{
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        request.Content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
    }

    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    context.Response.StatusCode = (int)response.StatusCode;
    if (response.Headers.Location is not null)
        context.Response.Headers["Location"] = response.Headers.Location.ToString();
    if (response.Content.Headers.ContentType is not null)
        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
    await response.Content.CopyToAsync(context.Response.Body, ct);
}

public partial class Program { }
