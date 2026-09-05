using System.Net;
using System.Security.Cryptography;
using System.Text;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using FullWorth.Banking.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

FullWorth.Shared.SecretBootstrap.AddSecretFiles(builder.Configuration);

builder.Services.AddOpenApi();
builder.Services.Configure<EnableBankingOptions>(builder.Configuration.GetSection(EnableBankingOptions.SectionName));
builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection(BackendOptions.SectionName));
builder.Services.Configure<BankingSyncOptions>(builder.Configuration.GetSection(BankingSyncOptions.SectionName));
builder.Services.AddSingleton<EnableBankingRequestPolicy>();
builder.Services.AddSingleton<BankSyncConcurrencyGate>();

builder.Services.AddHttpClient("enable-banking", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<EnableBankingOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(90);
});
// Legacy/global provider remains injectable for old connections and tests. New connections use resolver.
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
builder.Services.AddScoped<EnableBankingClientResolver>();
builder.Services.AddScoped<EnableBankingProfileService>();
builder.Services.AddScoped<BankSyncService>();
builder.Services.AddHostedService<BankSyncWorker>();

var app = builder.Build();

FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Security:ApiKey");
FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Backend:IngestKey");

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "fullworth-banking" }));

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var configured = builder.Configuration["Security:ApiKey"];
        var supplied = context.Request.Headers["X-FullWorth-Banking-Key"].ToString();
        if (!ValidKey(supplied, configured))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});

// Per-user BYO Enable Banking setup. The private key enters only this internal service path and is
// persisted encrypted by FullWorth.Backend; every read response below is a safe view without key data.
app.MapGet("/api/banking/status", async (
    HttpContext http,
    EnableBankingProfileService profiles,
    CancellationToken ct) =>
{
    if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
    return Results.Ok(await profiles.GetStatusAsync(userId, ct));
});

app.MapGet("/api/banking/profile", async (
    HttpContext http,
    EnableBankingProfileService profiles,
    CancellationToken ct) =>
{
    if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
    return Results.Ok(await profiles.GetStatusAsync(userId, ct));
});

app.MapPost("/api/banking/profile/verify", async (
    HttpContext http,
    EnableBankingProfileVerifyRequest request,
    EnableBankingProfileService profiles,
    CancellationToken ct) =>
{
    if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
    try
    {
        return Results.Ok(await profiles.VerifyAndSaveAsync(userId, request, ct));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = "invalid_profile", message = ex.Message });
    }
    catch (EnableBankingApiException)
    {
        return Results.BadRequest(new { error = "enable_banking_verification_failed", message = "Enable Banking rejected the application ID/private key." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = "enable_banking_verification_failed", message = ex.Message });
    }
});

app.MapPost("/api/banking/profile/recheck", async (
    HttpContext http,
    EnableBankingProfileService profiles,
    CancellationToken ct) =>
{
    if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
    try
    {
        return Results.Ok(await profiles.RecheckAsync(userId, ct));
    }
    catch (EnableBankingProfileNotConfiguredException)
    {
        return Results.NotFound();
    }
    catch (EnableBankingApiException)
    {
        return Results.BadRequest(new { error = "enable_banking_verification_failed" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = "enable_banking_verification_failed", message = ex.Message });
    }
});

app.MapDelete("/api/banking/profile", async (
    HttpContext http,
    EnableBankingProfileService profiles,
    CancellationToken ct) =>
{
    if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
    return await profiles.DeleteAsync(userId, ct) switch
    {
        HttpStatusCode.NoContent => Results.NoContent(),
        HttpStatusCode.Conflict => Results.Conflict(new { error = "profile_in_use" }),
        _ => Results.NotFound()
    };
});

app.MapGet("/api/banking/institutions", async (
    HttpContext http,
    string? country,
    string? psuType,
    BankSyncService service,
    CancellationToken ct) =>
{
    if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
    try
    {
        return Results.Json(await service.GetInstitutionsAsync(country, psuType, caller, ct));
    }
    catch (EnableBankingProfileNotConfiguredException ex)
    {
        return Results.Conflict(new { error = "banking_profile_not_ready", message = ex.Message });
    }
    catch (EnableBankingApiException ex)
    {
        return ProviderApiError(ex, consentAware: false);
    }
});

app.MapPost("/api/banking/connect", async (
    HttpContext http,
    ConnectBankRequest request,
    BankSyncService service,
    CancellationToken ct) =>
{
    if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
    try
    {
        return Results.Ok(new { authorizationUrl = await service.StartConnectionAsync(request, caller, ct) });
    }
    catch (BankAccessException exception)
    {
        return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound();
    }
    catch (EnableBankingProfileNotConfiguredException ex)
    {
        return Results.Conflict(new { error = "banking_profile_not_ready", message = ex.Message });
    }
    catch (EnableBankingApiException ex)
    {
        return ProviderApiError(ex, consentAware: false);
    }
});

// No browser-facing global "sync all" endpoint. Only the background worker may drive all tenants.
app.MapPost("/api/banking/connections/{id:guid}/sync", async (
    HttpContext http,
    Guid id,
    bool? force,
    BankSyncService service,
    CancellationToken ct) =>
{
    if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });

    var result = await service.RequestManualSyncAsync(id, caller, force ?? true, BuildPsuContext(http), ct);
    if (result.Status == ManualSyncStatus.NotFound) return Results.NotFound();

    var status = result.Status switch
    {
        ManualSyncStatus.Started => "completed",
        ManualSyncStatus.PartialHistory => "partial_history",
        ManualSyncStatus.Error => "error",
        ManualSyncStatus.Cooldown => "cooldown",
        ManualSyncStatus.AlreadyRunning => "already_running",
        ManualSyncStatus.ReauthorizationRequired => "reauthorization_required",
        _ => "unknown"
    };
    return Results.Ok(new { status, nextSyncAllowedAt = result.NextSyncAllowedAt });
});

app.MapDelete("/api/banking/connections/{id:guid}", async (
    HttpContext http,
    Guid id,
    bool? deleteLocalData,
    BankSyncService service,
    CancellationToken ct) =>
{
    if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });

    return await service.DisconnectAsync(
        id,
        caller,
        BuildPsuContext(http),
        deleteLocalData ?? true,
        ct) switch
    {
        DisconnectStatus.Deleted => Results.NoContent(),
        DisconnectStatus.ClosedDataRetained => Results.Ok(new { status = "closed_data_retained" }),
        DisconnectStatus.ProviderFailed => Results.StatusCode(StatusCodes.Status502BadGateway),
        _ => Results.NotFound()
    };
});

app.MapGet("/api/banking/transactions/{id:guid}/details", async (
    HttpContext http,
    Guid id,
    BankSyncService service,
    CancellationToken ct) =>
{
    if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
    try
    {
        return Results.Json(await service.GetTransactionDetailsAsync(id, caller, BuildPsuContext(http), ct));
    }
    catch (BankAccessException)
    {
        return Results.NotFound();
    }
    catch (BankReauthorizationRequiredException)
    {
        return Results.Conflict(new { error = "reauthorization_required" });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = "transaction_details_unavailable", message = ex.Message });
    }
    catch (EnableBankingApiException ex)
    {
        return ProviderApiError(ex, consentAware: true);
    }
});

app.MapGet("/connect/enable-banking/callback", async (
    HttpContext http,
    string? code,
    string? state,
    string? error,
    string? error_description,
    BankSyncService service,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error))
        return CallbackErrorRedirect(error, error_description);
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        return CallbackErrorRedirect("app_missing_parameters", null);

    try
    {
        var connection = await service.CompleteConnectionAsync(state, code, BuildPsuContext(http), ct);
        return Results.Redirect($"/?bankConnected={Uri.EscapeDataString(connection.InstitutionName)}");
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Enable Banking callback failed for state {State}.", SanitizeCallbackValue(state, 64));
        return CallbackErrorRedirect("app_invalid_callback", null);
    }
});

app.Run();

static bool TryGetUser(HttpContext http, out Guid userId) =>
    Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out userId) && userId != Guid.Empty;

static bool TryGetCaller(HttpContext http, out BankingCaller caller)
{
    caller = new BankingCaller(Guid.Empty, Guid.Empty);
    if (!TryGetUser(http, out var userId)) return false;
    if (!Guid.TryParse(http.Request.Headers["X-FullWorth-Space-Id"], out var spaceId) || spaceId == Guid.Empty) return false;
    caller = new BankingCaller(userId, spaceId);
    return true;
}

static PsuContext? BuildPsuContext(HttpContext http)
{
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var name in new[]
    {
        "Psu-Ip-Address",
        "Psu-User-Agent",
        "Psu-Referer",
        "Psu-Accept",
        "Psu-Accept-Charset",
        "Psu-Accept-Encoding",
        "Psu-Accept-language",
        "Psu-Geo-Location"
    })
    {
        var value = http.Request.Headers[name].ToString();
        if (!string.IsNullOrWhiteSpace(value)) headers[name] = value;
    }

    return headers.Count == 0 ? null : new PsuContext(headers);
}

static bool ValidKey(string supplied, string? configured)
{
    if (string.IsNullOrWhiteSpace(configured) || supplied.Length != configured.Length) return false;
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured));
}

static IResult ProviderApiError(EnableBankingApiException exception, bool consentAware)
{
    var classification = EnableBankingErrorClassifier.Classify(exception);

    if (classification.Category == BankErrorCategory.RateLimit)
        return Results.Json(
            new { error = classification.Code, message = classification.SafeMessage, retryAt = classification.RetryAt },
            statusCode: StatusCodes.Status429TooManyRequests);

    if (consentAware &&
        classification.Category is BankErrorCategory.AuthRequired or BankErrorCategory.ConsentExpired)
        return Results.Conflict(new { error = "reauthorization_required" });

    if (!consentAware &&
        classification.Category is BankErrorCategory.AuthRequired or BankErrorCategory.ConsentExpired)
        return Results.Conflict(new
        {
            error = "enable_banking_auth_failed",
            message = "Enable Banking application authentication failed. Recheck the configured application."
        });

    if (classification.Category == BankErrorCategory.PsuContext)
        return Results.Conflict(new { error = classification.Code, message = classification.SafeMessage });

    if (classification.Category == BankErrorCategory.TransientProvider)
        return Results.Json(
            new { error = classification.Code, message = classification.SafeMessage },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Json(
        new { error = classification.Code, message = classification.SafeMessage },
        statusCode: StatusCodes.Status502BadGateway);
}

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

public partial class Program { }
