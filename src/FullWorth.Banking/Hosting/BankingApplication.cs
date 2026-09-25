using System.Net;
using System.Security.Cryptography;
using System.Text;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using FullWorth.Banking.Services;
using FullWorth.FinTs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Hosting;

/// <summary>
/// Reusable banking module for the standalone banking executable and the unified FullWorth host.
/// </summary>
internal sealed class BankingLogCategory { }

public static class BankingApplication
{
    public static void AddFullWorthBanking(this WebApplicationBuilder builder, bool unifiedHost = false)
    {
        if (!unifiedHost)
                    builder.Services.AddOpenApi();
        builder.Services.Configure<EnableBankingOptions>(builder.Configuration.GetSection(EnableBankingOptions.SectionName));
        builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection(BackendOptions.SectionName));
        // Blank means "wherever this container keeps its backend", which in the unified image is
        // loopback. The split-image hostname used to be the hardcoded default here as well.
        builder.Services.PostConfigure<BackendOptions>(options =>
            options.BaseUrl = FullWorth.Shared.UnifiedHost.BackendBaseUrlForBanking(builder.Configuration));
        builder.Services.Configure<BankingSyncOptions>(builder.Configuration.GetSection(BankingSyncOptions.SectionName));
        builder.Services.Configure<BankingProviderStatusOptions>(builder.Configuration.GetSection(BankingProviderStatusOptions.SectionName));
        builder.Services.Configure<BankingInstitutionCatalogOptions>(builder.Configuration.GetSection(BankingInstitutionCatalogOptions.SectionName));
        builder.Services.Configure<FinTsOptions>(builder.Configuration.GetSection(FinTsOptions.SectionName));
        builder.Services.AddSingleton<EnableBankingRequestPolicy>();
        builder.Services.AddSingleton<BankSyncConcurrencyGate>();
        
        builder.Services.AddHttpClient("enable-banking", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<EnableBankingOptions>>().CurrentValue;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        builder.Services.AddHttpClient("enable-banking-control-panel", (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<EnableBankingOptions>>().CurrentValue;
            client.BaseAddress = new Uri(options.ControlPanelBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        // Legacy/global provider remains injectable for old connections and tests. New connections use resolver.
        builder.Services.AddHttpClient<EnableBankingClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptionsMonitor<EnableBankingOptions>>().CurrentValue;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(90);
        });
        builder.Services.AddHttpClient<FullWorthBackendClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        builder.Services.AddHttpClient("fints", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        builder.Services.AddScoped<IFinTsTransport>(services =>
            new FinTsHttpTransport(services.GetRequiredService<IHttpClientFactory>().CreateClient("fints")));
        builder.Services.AddScoped<FinTsClient>();
        builder.Services.AddScoped<IngFinTsService>();
        builder.Services.AddScoped<EnableBankingClientResolver>();
        builder.Services.AddScoped<EnableBankingProfileService>();
        builder.Services.AddSingleton<EnableBankingControlPanelRegistrationService>();
        builder.Services.AddSingleton<EnableBankingControlPanelStatusService>();
        // TryAdd: in the unified host FullWorth.Web has registered TimeProvider.System already.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddScoped<BankSyncService>();
        builder.Services.AddHostedService<BankSyncWorker>();
        builder.Services.AddHostedService<BankingProviderStatusWorker>();
        builder.Services.AddHostedService<BankingInstitutionCatalogWorker>();
    }

    public static void InitializeFullWorthBanking(this WebApplication app)
    {
        FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Security:ApiKey");
        FullWorth.Shared.SecretBootstrap.RequireSecret(app.Configuration, app.Environment, "Backend:IngestKey");
    }

    public static void UseFullWorthBanking(this WebApplication app, bool unifiedHost = false)
    {
        if (!unifiedHost)
        {
            if (app.Environment.IsDevelopment()) app.MapOpenApi();
            app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "fullworth-banking" }));
            ConfigureBankingMiddleware(app, app.Configuration);
        }
        else
        {
            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/api/banking"),
                branch => ConfigureBankingMiddleware(branch, app.Configuration));
        }

        // Banking authenticates its internal browser bridge with X-FullWorth-Banking-Key and trusted
        // user headers; callbacks are intentionally public. Do not apply the Web fallback cookie policy.
        var endpoints = app.MapGroup(string.Empty).AllowAnonymous();
        endpoints.MapGet("/api/banking/status", async (
            HttpContext http,
            EnableBankingProfileService profiles,
            CancellationToken ct) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            return Results.Ok(await profiles.GetStatusAsync(userId, ct));
        });
        
        endpoints.MapGet("/api/banking/profile", async (
            HttpContext http,
            EnableBankingProfileService profiles,
            CancellationToken ct) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            return Results.Ok(await profiles.GetStatusAsync(userId, ct));
        });
        
        endpoints.MapPost("/api/banking/profile/verify", async (
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
        
        endpoints.MapPost("/api/banking/profile/recheck", async (
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
        
        endpoints.MapDelete("/api/banking/profile", async (
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
        
        endpoints.MapPost("/api/banking/profile/register/start", async (
            HttpContext http,
            EnableBankingAutoRegistrationRequest request,
            EnableBankingControlPanelRegistrationService registration,
            CancellationToken ct) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                return Results.Ok(await registration.StartAsync(userId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_registration_request", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = "registration_unavailable", message = ex.Message });
            }
            catch (EnableBankingControlPanelException ex)
            {
                return Results.Json(
                    new { error = ex.SafeCode },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
        
        endpoints.MapGet("/api/banking/profile/register/{id}", (
            HttpContext http,
            string id,
            EnableBankingControlPanelRegistrationService registration) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            // Never 404: an unknown id is reported as expired so the wizard fails the step instead of
            // polling a 404 for as long as the page stays open.
            return Results.Ok(registration.GetStatus(userId, id));
        });
        
        endpoints.MapPost("/api/banking/profile/register/{id}/retry", async (
            HttpContext http,
            string id,
            EnableBankingControlPanelRegistrationService registration,
            CancellationToken ct) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                return Results.Ok(await registration.RetryVerificationAsync(userId, id, ct));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = "verification_retry_unavailable", message = ex.Message });
            }
        });
        
        endpoints.MapDelete("/api/banking/profile/register/{id}", (
            HttpContext http,
            string id,
            EnableBankingControlPanelRegistrationService registration) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            return registration.Cancel(userId, id) ? Results.NoContent() : Results.NotFound();
        });
        
        // #169: liest den lokal gehaltenen Katalog. Vorher rief dieser Endpunkt bei jedem Oeffnen des
        // Bankdialogs /aspsps beim Anbieter - die Bankauswahl hing damit an dessen Erreichbarkeit und
        // Latenz, und zwar an dem Teil, ohne den man gar keine Bank auswaehlen kann. Aktuell gehalten
        // wird der Katalog von BankingInstitutionCatalogWorker.
        //
        // Die Antwortform bleibt {aspsps:[...]} mit den Feldern des Anbieters, damit die Oberflaeche
        // unveraendert weiterliest - geaendert hat sich nur, woher sie kommt.
        endpoints.MapGet("/api/banking/institutions", async (
            HttpContext http,
            string? country,
            string? psuType,
            FullWorthBackendClient backend,
            BankSyncService service,
            IOptionsMonitor<EnableBankingOptions> providerOptions,
            CancellationToken ct) =>
        {
            if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });

            var normalized = (country ?? providerOptions.CurrentValue.DefaultCountry ?? "DE").Trim().ToUpperInvariant();
            if (normalized.Length != 2)
                return Results.BadRequest(new { error = "invalid_institution_query", message = "Country must be a two-letter code." });

            var catalog = await backend.GetInstitutionCatalogAsync(normalized, ct);

            // Einmaliges Fuellen bei kaltem Katalog, und nur dann.
            //
            // Ohne das waere der erste Eindruck schlechter als der Zustand vor #169: wer Enable Banking
            // gerade eingerichtet hat, saehe eine leere Bankauswahl, bis der Hintergrunddienst das
            // naechste Mal laeuft - das kann ein Tag sein. Die Forderung des Issues, dass NORMALE
            // UI-Reads keinen externen Abruf ausloesen, bleibt erfuellt: der Normalfall ist der warme
            // Katalog, und der geht nie nach draussen. Dieser Zweig ist das erste Mal, nicht der
            // Regelfall - danach haelt der Dienst ihn aktuell.
            if (catalog is null || !catalog.Known)
            {
                try
                {
                    var fresh = await service.GetInstitutionsAsync(normalized, psuType: null, caller, ct);
                    if (BankingInstitutionPayload.TryRead(fresh, normalized, out var rows))
                    {
                        await backend.ReplaceInstitutionCatalogAsync(normalized, rows, ct);
                        catalog = await backend.GetInstitutionCatalogAsync(normalized, ct);
                    }
                }
                catch (EnableBankingProfileNotConfiguredException ex)
                {
                    return Results.Conflict(new { error = "banking_profile_not_ready", message = ex.Message });
                }
                catch (EnableBankingApiException ex)
                {
                    return ProviderApiError(ex, consentAware: false);
                }
            }

            // Auch nach dem Versuch nichts: dann ist der Zugang nicht bereit. Ein leeres Verzeichnis
            // auszugeben saehe aus, als gaebe es in diesem Land keine Bank.
            if (catalog is null || !catalog.Known)
                return Results.Conflict(new
                {
                    error = "banking_profile_not_ready",
                    message = "The institution catalogue has not been fetched yet."
                });

            var rows2 = catalog.Institutions
                // Der PSU-Typ-Filter bleibt erhalten, nur wird er jetzt hier angewandt statt vom
                // Anbieter: gespeichert ist bewusst der ungefilterte Katalog, damit ein Wechsel des
                // Filters keinen neuen Abruf braucht.
                .Where(row => psuType is null || MatchesPsuType(row, psuType))
                .Select(row => new Dictionary<string, object?>
                {
                    ["name"] = row.Name,
                    ["country"] = row.Country,
                    ["psu_types"] = row.PsuTypes,
                    ["group"] = row.Group,
                    ["logo"] = row.Logo,
                    ["beta"] = row.Beta,
                    ["auth_methods"] = row.AuthMethods
                })
                .ToList();

            return Results.Ok(new { aspsps = rows2 });
        });
        
        // #165: liest ausschliesslich den lokal gespeicherten Stand. Vorher holte dieser Endpunkt den
        // Zustand bei JEDEM Aufruf live aus dem Control Panel - Token holen, notfalls erneuern,
        // /api/get_today_stats lesen, bei 401 alles noch einmal - und der Bankdialog wartete darauf.
        // Aktuell gehalten wird der Stand jetzt von BankingProviderStatusWorker, unabhaengig davon,
        // ob gerade jemand hinsieht.
        endpoints.MapGet("/api/banking/provider-status", async (
            HttpContext http,
            string? country,
            FullWorthBackendClient backend,
            CancellationToken ct) =>
        {
            if (!TryGetUser(http, out _)) return Results.BadRequest(new { error = "missing_user_context" });

            var snapshot = await backend.GetProviderStatusAsync(country, ct);
            // Noch nie erfolgreich geprueft heisst "unbekannt", nicht "alles in Ordnung": eine leere
            // Liste als gesund zu lesen wuerde nach einer frischen Installation jede Bank als erreichbar
            // ausgeben, obwohl niemand nachgesehen hat. Die Oberflaeche bleibt in beiden Faellen voll
            // benutzbar - die Bankenliste haengt nicht mehr an dieser Antwort.
            if (snapshot is null || !snapshot.Known)
                return Results.Ok(new EnableBankingProviderStatusView(
                    Available: false,
                    Reason: snapshot?.LastError ?? "provider_status_unknown",
                    CheckedAt: snapshot?.LastAttemptAt ?? DateTimeOffset.UtcNow,
                    Statuses: []));

            return Results.Ok(new EnableBankingProviderStatusView(
                Available: true,
                // Der letzte Versuch ist gescheitert, der gespeicherte Stand gilt aber weiter: der Grund
                // steht dabei, damit die Oberflaeche ihn als veraltet kennzeichnen kann, statt ihn zu
                // verlieren oder als frisch auszugeben.
                Reason: snapshot.LastError,
                CheckedAt: snapshot.LastSuccessfulAt ?? DateTimeOffset.UtcNow,
                Statuses: snapshot.Statuses
                    .Select(row => new EnableBankingAspspStatusView(row.Country, row.Brand, row.PsuType, row.Status))
                    .ToList()));
        });
        
        endpoints.MapPost("/api/banking/provider-status/connect/start", async (
            HttpContext http,
            EnableBankingProviderStatusConnectRequest request,
            EnableBankingControlPanelStatusService statusService,
            CancellationToken ct) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                return Results.Ok(await statusService.StartConnectionAsync(userId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_status_connection", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = "status_connection_unavailable", message = ex.Message });
            }
        });
        
        endpoints.MapPost("/api/banking/provider-status/connect/complete", async (
            HttpContext http,
            EnableBankingProviderStatusConnectCompleteRequest request,
            EnableBankingControlPanelStatusService statusService,
            CancellationToken ct) =>
        {
            if (!TryGetUser(http, out var userId)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                return Results.Ok(await statusService.CompleteConnectionManuallyAsync(userId, request, ct));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_status_connection", message = ex.Message });
            }
        });
        
        endpoints.MapPost("/api/banking/fints/ing/connect", async (
            HttpContext http,
            ConnectIngFinTsRequest request,
            IngFinTsService service,
            CancellationToken ct) =>
        {
            if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                return Results.Ok(await service.ConnectAsync(request, caller, ct));
            }
            catch (BankAccessException exception)
            {
                return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_fints_request", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = "fints_not_configured", message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (FinTsException ex)
            {
                return Results.BadRequest(new { error = ex.Code ?? "fints_error", message = "ING FinTS rejected the request." });
            }
        });
        
        // Die Auswahl: was die Bank gemeldet hat, ohne sie erneut zu fragen.
        endpoints.MapGet("/api/banking/fints/connections/{id:guid}/accounts", async (
            HttpContext http,
            Guid id,
            IngFinTsService service,
            CancellationToken ct) =>
        {
            if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
            try { return Results.Ok(await service.DiscoveredAsync(id, caller, ct)); }
            catch (BankAccessException exception)
            {
                return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = "fints_secret_missing", message = ex.Message });
            }
        });

        // Die Uebernahme: erst die Auswahl festhalten, dann holen. Der lange Teil liegt hier, nicht im
        // Verbinden - deshalb weiss der Benutzer beim Warten, worauf er wartet.
        endpoints.MapPost("/api/banking/fints/connections/{id:guid}/import", async (
            HttpContext http,
            Guid id,
            FinTsImportRequest request,
            IngFinTsService service,
            CancellationToken ct) =>
        {
            if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
            try { return Results.Ok(await service.ImportAsync(id, request.Hidden ?? [], caller, ct)); }
            catch (BankAccessException exception)
            {
                return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = "fints_secret_missing", message = ex.Message });
            }
            catch (FinTsException ex)
            {
                return Results.BadRequest(new { error = ex.Code ?? "fints_error", message = "ING FinTS rejected the request." });
            }
        });

        endpoints.MapPost("/api/banking/fints/connections/{id:guid}/tan", async (
            HttpContext http,
            Guid id,
            FinTsTanSubmitRequest request,
            IngFinTsService service,
            CancellationToken ct) =>
        {
            if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                return Results.Ok(await service.ContinueTanAsync(id, caller, request.Tan, poll: false, ct));
            }
            catch (BankAccessException exception)
            {
                return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_fints_tan", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = "fints_tan_unavailable", message = ex.Message });
            }
            catch (FinTsException ex)
            {
                return Results.BadRequest(new { error = ex.Code ?? "fints_error", message = "ING FinTS rejected the TAN." });
            }
        });
        
        // The challenge a stored connection is waiting on, so a TAN can be answered outside the dialog
        // that started it. 204 when nothing is pending; the login and PIN never leave the service.
        endpoints.MapGet("/api/banking/fints/connections/{id:guid}/challenge", async (
            HttpContext http,
            Guid id,
            IngFinTsService service,
            CancellationToken ct) =>
        {
            if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                var pending = await service.PendingChallengeAsync(id, caller, ct);
                return pending is null ? Results.NoContent() : Results.Ok(pending);
            }
            catch (BankAccessException exception)
            {
                return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound();
            }
        });

        endpoints.MapPost("/api/banking/fints/connections/{id:guid}/poll", async (
            HttpContext http,
            Guid id,
            IngFinTsService service,
            CancellationToken ct) =>
        {
            if (!TryGetCaller(http, out var caller)) return Results.BadRequest(new { error = "missing_user_context" });
            try
            {
                return Results.Ok(await service.ContinueTanAsync(id, caller, null, poll: true, ct));
            }
            catch (BankAccessException exception)
            {
                return exception.Forbidden ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = "fints_poll_unavailable", message = ex.Message });
            }
            catch (FinTsException ex)
            {
                return Results.BadRequest(new { error = ex.Code ?? "fints_error", message = "ING FinTS rejected the status request." });
            }
        });
        
        endpoints.MapPost("/api/banking/connect", async (
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
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_connect_request", message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = "invalid_connect_request", message = ex.Message });
            }
            catch (EnableBankingApiException ex)
            {
                return ProviderApiError(ex, consentAware: false);
            }
        });
        
        // No browser-facing global "sync all" endpoint. Only the background worker may drive all tenants.
        endpoints.MapPost("/api/banking/connections/{id:guid}/sync", async (
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
                ManualSyncStatus.TanRequired => "tan_required",
                _ => "unknown"
            };
            return Results.Ok(new { status, nextSyncAllowedAt = result.NextSyncAllowedAt });
        });
        
        endpoints.MapDelete("/api/banking/connections/{id:guid}", async (
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
        
        endpoints.MapGet("/api/banking/transactions/{id:guid}/details", async (
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
        
        endpoints.MapGet("/connect/enable-banking/status-callback", async (
            string? state,
            string? oobCode,
            EnableBankingControlPanelStatusService statusService,
            CancellationToken ct) =>
        {
            var result = await statusService.CompleteConnectionAsync(state, oobCode, ct);
            var title = result.Success ? "Enable Banking bank status connected" : "Enable Banking bank status connection failed";
            var message = result.Success
                ? "FullWorth can now read the Enable Banking bank-status feed. Return to FullWorth."
                : "The bank-status sign-in could not be completed. Return to FullWorth and try again.";
            var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{title}</title></head><body style=\"font-family:system-ui,sans-serif;max-width:640px;margin:64px auto;padding:0 24px\"><h1>{title}</h1><p>{message}</p></body></html>";
            return Results.Content(
                html,
                "text/html; charset=utf-8",
                Encoding.UTF8,
                result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });
        
        endpoints.MapGet("/connect/enable-banking/setup-callback", async (
            string? state,
            string? oobCode,
            EnableBankingControlPanelRegistrationService registration,
            CancellationToken ct) =>
        {
            var result = await registration.CompleteAsync(state, oobCode, ct);
            var title = result.Success ? "Enable Banking setup completed" : "Enable Banking setup failed";
            var message = result.Success
                ? "The application was registered. Return to FullWorth; the setup dialog will update automatically."
                : "The automatic setup could not be completed. Return to FullWorth and try again or use the manual setup.";
            var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{title}</title></head><body style=\"font-family:system-ui,sans-serif;max-width:640px;margin:64px auto;padding:0 24px\"><h1>{title}</h1><p>{message}</p></body></html>";
            return Results.Content(
                html,
                "text/html; charset=utf-8",
                Encoding.UTF8,
                result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        });
        
        endpoints.MapGet("/connect/enable-banking/callback", async (
            HttpContext http,
            string? code,
            string? state,
            string? error,
            string? error_description,
            BankSyncService service,
            ILogger<BankingLogCategory> logger,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                if (!string.IsNullOrWhiteSpace(state))
                {
                    try
                    {
                        await service.HandleAuthorizationErrorAsync(state, error, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // The browser must still get a safe callback result; never reflect provider/state
                        // details. A failed cleanup is logged and the expiring state remains server-side.
                        logger.LogWarning(
                            exception,
                            "Enable Banking authorization-error callback cleanup failed for state {State}.",
                            SanitizeCallbackValue(state, 64));
                    }
                }
                return CallbackErrorRedirect(error, error_description);
            }
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
                return CallbackErrorRedirect("app_missing_parameters", null);
        
            try
            {
                var connection = await service.CompleteConnectionAsync(state, code, BuildPsuContext(http), ct);
                if (connection.Status.ToUpperInvariant() is "EXPIRED" or "REVOKED" or "CLOSED" or "INVALID" or "CANCELLED")
                    return CallbackErrorRedirect("reauthorization_required", null);
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
    }

    private static void ConfigureBankingMiddleware(IApplicationBuilder app, IConfiguration configuration)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var configured = configuration["Security:ApiKey"];
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
    }

    private static bool TryGetUser(HttpContext http, out Guid userId) =>
        Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out userId) && userId != Guid.Empty;
    
    private static bool TryGetCaller(HttpContext http, out BankingCaller caller)
    {
        caller = new BankingCaller(Guid.Empty, Guid.Empty);
        if (!TryGetUser(http, out var userId)) return false;
        if (!Guid.TryParse(http.Request.Headers["X-FullWorth-Space-Id"], out var spaceId) || spaceId == Guid.Empty) return false;
        caller = new BankingCaller(userId, spaceId);
        return true;
    }
    
    private static PsuContext? BuildPsuContext(HttpContext http)
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
    
    private static bool ValidKey(string supplied, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || supplied.Length != configured.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured));
    }
    
    private static IResult ProviderApiError(EnableBankingApiException exception, bool consentAware)
    {
        var classification = EnableBankingErrorClassifier.Classify(exception);
    
        if (classification.Category == BankErrorCategory.RateLimit)
            return Results.Json(
                new { error = classification.Code, message = classification.SafeMessage, retryAt = classification.RetryAt },
                statusCode: StatusCodes.Status429TooManyRequests);
    
        if (consentAware &&
            classification.Category is BankErrorCategory.AuthRequired or BankErrorCategory.ConsentExpired)
            return Results.Conflict(new { error = "reauthorization_required" });
    
        if (classification.Category == BankErrorCategory.ApplicationAuth)
            return Results.Conflict(new
            {
                error = classification.Code,
                message = classification.SafeMessage
            });
    
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
    
    private static IResult CallbackErrorRedirect(string errorCode, string? description)
    {
        var url = $"/?bankError={Uri.EscapeDataString(SanitizeCallbackValue(errorCode, 64) ?? "unknown")}";
        var detail = SanitizeCallbackValue(description, 180);
        if (detail is not null) url += $"&bankErrorDescription={Uri.EscapeDataString(detail)}";
        return Results.Redirect(url);
    }
    
    private static string? SanitizeCallbackValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Trim().Where(character => !char.IsControl(character)).ToArray());
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    /// <summary>
    /// Ob ein Katalogeintrag zum gewuenschten PSU-Typ passt (#169). Ein Eintrag ohne Angabe passt
    /// immer: der Anbieter laesst das Feld weg, wenn die Bank keine Unterscheidung macht, und ihn
    /// wegzufiltern liesse genau diese Banken verschwinden.
    /// </summary>
    private static bool MatchesPsuType(BankingInstitutionRowDto row, string psuType)
    {
        if (row.PsuTypes is not { ValueKind: System.Text.Json.JsonValueKind.Array } types) return true;
        var wanted = psuType.Trim();
        var any = false;
        foreach (var item in types.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            any = true;
            if (string.Equals(item.GetString(), wanted, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return !any;
    }
}
