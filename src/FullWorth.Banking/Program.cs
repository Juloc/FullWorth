using System.Security.Cryptography;
using System.Text;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using FullWorth.Banking.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// P0.3: Docker secret files (build-time, before any config read).
FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.Configure<EnableBankingOptions>(builder.Configuration.GetSection(EnableBankingOptions.SectionName));
builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection(BackendOptions.SectionName));
builder.Services.Configure<BankingSyncOptions>(builder.Configuration.GetSection(BankingSyncOptions.SectionName));
builder.Services.AddSingleton<EnableBankingRequestPolicy>();
builder.Services.AddSingleton<BankSyncConcurrencyGate>();
builder.Services.AddHttpClient<EnableBankingClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<EnableBankingOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient<FullWorthBackendClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddScoped<BankSyncService>();
builder.Services.AddHostedService<BankSyncWorker>();

var app = builder.Build();

// P0.3 fail-closed against the fully-merged configuration (Production only; no-op in dev/test).
FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Security:ApiKey");
FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Backend:IngestKey");

// P1.2c: OpenAPI document is Development-only, never exposed in Production.
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "fullworth-banking" }));

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var configured = builder.Configuration["Security:ApiKey"];
        var supplied = context.Request.Headers["X-FullWorth-Banking-Key"].ToString();
        if (!ValidKey(supplied, configured)) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

        // Without Enable Banking credentials every provider call would die in an unhandled
        // InvalidOperationException (500). Answer with an explicit, machine-readable 503 instead so
        // the UI can tell the operator what is missing. /api/banking/status stays reachable.
        if (!context.Request.Path.StartsWithSegments("/api/banking/status"))
        {
            var options = context.RequestServices.GetRequiredService<IOptions<EnableBankingOptions>>().Value;
            if (!ProviderConfigured(options))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "banking_not_configured",
                    message = "Enable Banking is not configured. Set EnableBanking:ApplicationId, EnableBanking:RedirectUrl and provide the private key."
                });
                return;
            }
        }
    }
    await next();
});

app.MapGet("/api/banking/status", (IOptions<EnableBankingOptions> options) =>
    Results.Ok(new { configured = ProviderConfigured(options.Value) }));

app.MapGet("/api/banking/institutions", async (string? country, BankSyncService service, CancellationToken ct) => Results.Json(await service.GetInstitutionsAsync(country, ct)));
app.MapPost("/api/banking/connect", async (HttpContext http, ConnectBankRequest request, BankSyncService service, CancellationToken ct) =>
{
    if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
    try { return Results.Ok(new { authorizationUrl = await service.StartConnectionAsync(request, caller, ct) }); }
    catch (BankAccessException exception) { return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound(); }
});
app.MapPost("/api/banking/sync", async (BankSyncService service, CancellationToken ct) => Results.Ok(await service.SyncAllAsync(ct)));
app.MapPost("/api/banking/connections/{id:guid}/sync", async (HttpContext http, Guid id, bool? force, BankSyncService service, CancellationToken ct) =>
{
    if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
    // The "sync now" button is a deliberate user action for current data, so this endpoint forces by
    // default; pass ?force=false to honour the background cadence cooldown instead.
    var result = await service.RequestManualSyncAsync(id, caller, force ?? true, ct);
    if (result.Status == ManualSyncStatus.NotFound) return Results.NotFound();
    var status = result.Status switch
    {
        // RequestManualSyncAsync waits for SyncConnectionCoreAsync to finish before returning. Calling
        // this "started" made a completed ingest look like it was still running in the Web UI.
        ManualSyncStatus.Started => "completed",
        ManualSyncStatus.Cooldown => "cooldown",
        ManualSyncStatus.AlreadyRunning => "already_running",
        ManualSyncStatus.ReauthorizationRequired => "reauthorization_required",
        _ => "unknown"
    };
    return Results.Ok(new { status, nextSyncAllowedAt = result.NextSyncAllowedAt });
});
app.MapGet("/connect/enable-banking/callback", async (string? code, string? state, string? error, string? error_description, BankSyncService service, IOptions<EnableBankingOptions> options, ILogger<Program> logger, CancellationToken ct) =>
{
    // A PERSON lands here (the bank redirects the browser back). Every outcome — success, the user
    // cancelling at the bank, an expired state, a provider outage, missing configuration — must
    // redirect into the app UI where it is rendered as a localized message. Raw JSON or a 500 is
    // never an acceptable answer on this route.
    // Server-synthesized codes carry an app_ prefix so a crafted ?error=... from outside can never
    // impersonate an internal state (e.g. fake "not configured") in the UI.
    if (!ProviderConfigured(options.Value))
        return CallbackErrorRedirect("app_not_configured", null);
    if (!string.IsNullOrWhiteSpace(error))
        return CallbackErrorRedirect(error, error_description);
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        return CallbackErrorRedirect("app_missing_parameters", null);
    try
    {
        var connection = await service.CompleteConnectionAsync(state, code, ct);
        return Results.Redirect($"/?bankConnected={Uri.EscapeDataString(connection.InstitutionName)}");
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // Genuine cancellation (client gone / shutdown). Provider/backend TIMEOUTS also surface as
        // OperationCanceledException but with an uncancelled ct — those fall through to the redirect.
        throw;
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Enable Banking callback failed for state {State}.", SanitizeCallbackValue(state, 64));
        return CallbackErrorRedirect("app_invalid_callback", null);
    }
});

app.Run();

// The trusted user + space identity is set by FullWorth.Web from the authenticated session; the
// backend re-verifies ownership, so these headers alone grant nothing.
static bool TryGetCaller(HttpContext http, out BankingCaller caller)
{
    caller = new BankingCaller(Guid.Empty, Guid.Empty);
    if (!Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out var userId) || userId == Guid.Empty) return false;
    if (!Guid.TryParse(http.Request.Headers["X-FullWorth-Space-Id"], out var spaceId) || spaceId == Guid.Empty) return false;
    caller = new BankingCaller(userId, spaceId);
    return true;
}

static bool ValidKey(string supplied, string? configured)
{
    if (string.IsNullOrWhiteSpace(configured) || supplied.Length != configured.Length) return false;
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured));
}

static bool ProviderConfigured(EnableBankingOptions options) =>
    !string.IsNullOrWhiteSpace(options.ApplicationId)
    && !string.IsNullOrWhiteSpace(options.RedirectUrl)   // required: the connect flow derives the OAuth redirect from it
    && File.Exists(options.PrivateKeyPath);

// Redirect a failed bank-authorization callback into the app UI. Codes and descriptions come from an
// external redirect, so they are length-capped and control-character-stripped before being reflected
// as query parameters (the SPA renders them via textContent, never as markup).
static IResult CallbackErrorRedirect(string errorCode, string? description)
{
    var url = $"/?bankError={Uri.EscapeDataString(SanitizeCallbackValue(errorCode, 64) ?? "unknown")}";
    var detail = SanitizeCallbackValue(description, 180);
    if (detail is not null) url += $"&bankErrorDescription={Uri.EscapeDataString(detail)}";
    return Results.Redirect(url);
}

static string? SanitizeCallbackValue(string? value, int maxLength)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var cleaned = new string(value.Trim().Where(character => !char.IsControl(character)).ToArray());
    if (cleaned.Length == 0) return null;
    return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
}
