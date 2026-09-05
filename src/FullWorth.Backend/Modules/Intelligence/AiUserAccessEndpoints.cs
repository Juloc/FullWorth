using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record UpdateApiKeyAiAccessRequest(
    string ApiKey,
    string? TextModel,
    string? VisionModel);

public sealed record UpdateCustomAiAccessRequest(
    string BaseUrl,
    string AuthType,
    string? Username,
    string? Secret,
    string? TextModel,
    string? VisionModel);

public static class AiUserAccessEndpoints
{
    private const string DefaultOpenAiModel = "gpt-5.6-terra";
    private const string BridgeScopeHeader = "X-FullWorth-Codex-Scope";

    public static IEndpointRouteBuilder MapAiUserAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/access").WithTags("AI Access");

        group.MapGet("/", async (
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            IntelligenceStore store,
            IConfiguration configuration,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var settings = await db.AiUserSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId, ct);
            AiCredential? credential = null;
            if (settings?.CredentialId is { } credentialId)
                credential = await db.AiCredentials.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == credentialId && x.OwnerUserId == userId, ct);

            object? custom = null;
            if (credential?.Provider == IntelligenceProviders.OpenAiCompatible)
            {
                try
                {
                    var secret = await store.ResolveCredentialSecretAsync(credential.Id, userId, ct);
                    var parsed = OpenAiCompatibleCredentialCodec.Decode(secret);
                    custom = new
                    {
                        baseUrl = parsed.BaseUrl,
                        authType = parsed.AuthType,
                        username = parsed.Username
                    };
                }
                catch
                {
                    custom = null;
                }
            }

            bool? codexConnected = null;
            if (credential?.Provider == IntelligenceProviders.Codex)
                codexConnected = await TryGetCodexConnectedAsync(userId, configuration, clients, ct);

            return Results.Ok(new
            {
                configured = settings?.Enabled == true && credential is not null,
                mode = credential?.Provider switch
                {
                    IntelligenceProviders.Codex => "codex",
                    IntelligenceProviders.OpenAiCompatible => "custom",
                    IntelligenceProviders.OpenAi => "api-key",
                    _ => (string?)null
                },
                credential = credential is null ? null : new
                {
                    credential.Id,
                    credential.Provider,
                    credential.Name,
                    credential.SecretFingerprint,
                    credential.LastTestedAt,
                    credential.LastTestSucceeded
                },
                custom,
                codexConnected,
                textModel = settings?.TextModel,
                visionModel = settings?.VisionModel,
                enabled = settings?.Enabled ?? false
            });
        });

        group.MapPut("/api-key", async (
            UpdateApiKeyAiAccessRequest request,
            CurrentUserContext currentUser,
            IntelligenceStore store,
            IntelligenceProviderRegistry providers,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var apiKey = request.ApiKey?.Trim() ?? string.Empty;
            if (apiKey.Length is < 8 or > 8192)
                return Results.BadRequest(new { error = "invalid_api_key" });

            var test = await providers.GetRequired(IntelligenceProviders.OpenAi)
                .TestCredentialAsync(apiKey, ct);
            if (!test.Success)
                return Results.UnprocessableEntity(new { error = test.ErrorCode, message = test.Message });

            var credential = await store.CreateCredentialAsync(
                userId,
                IntelligenceProviders.OpenAi,
                "OpenAI API Key",
                apiKey,
                ct);
            await store.SelectUserCredentialAsync(
                userId,
                credential.Id,
                string.IsNullOrWhiteSpace(request.TextModel) ? DefaultOpenAiModel : request.TextModel,
                string.IsNullOrWhiteSpace(request.VisionModel) ? DefaultOpenAiModel : request.VisionModel,
                ct);
            await store.DeleteOtherUserCredentialsAsync(userId, credential.Id, ct);
            return Results.Ok(new { configured = true, mode = "api-key", credential });
        });

        group.MapPut("/custom", async (
            UpdateCustomAiAccessRequest request,
            CurrentUserContext currentUser,
            IntelligenceStore store,
            IntelligenceProviderRegistry providers,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            string encoded;
            try
            {
                encoded = OpenAiCompatibleCredentialCodec.Encode(new(
                    request.BaseUrl?.Trim() ?? string.Empty,
                    request.AuthType?.Trim().ToLowerInvariant() ?? string.Empty,
                    request.Username?.Trim(),
                    request.Secret));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_custom_endpoint", message = ex.Message });
            }

            if (string.IsNullOrWhiteSpace(request.TextModel))
                return Results.BadRequest(new { error = "custom_model_required", message = "A text model is required for a custom AI endpoint." });

            var test = await providers.GetRequired(IntelligenceProviders.OpenAiCompatible)
                .TestCredentialAsync(encoded, ct);
            if (!test.Success)
                return Results.UnprocessableEntity(new { error = test.ErrorCode, message = test.Message });

            var credential = await store.CreateCredentialAsync(
                userId,
                IntelligenceProviders.OpenAiCompatible,
                "Eigener AI Endpoint",
                encoded,
                ct);
            await store.SelectUserCredentialAsync(
                userId,
                credential.Id,
                request.TextModel,
                request.VisionModel,
                ct);
            await store.DeleteOtherUserCredentialsAsync(userId, credential.Id, ct);
            return Results.Ok(new { configured = true, mode = "custom", credential });
        });

        group.MapPost("/codex/login", async (
            CurrentUserContext currentUser,
            IConfiguration configuration,
            IHttpClientFactory clients,
            CancellationToken ct) =>
            await ForwardCodexAsync(
                HttpMethod.Post,
                "/auth/start",
                currentUser.RequireUserId(),
                "{}",
                configuration,
                clients,
                ct));

        group.MapGet("/codex/login/{sessionId:guid}", async (
            Guid sessionId,
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            IntelligenceStore store,
            IConfiguration configuration,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var result = await ForwardCodexJsonAsync(
                HttpMethod.Get,
                $"/auth/{sessionId:D}",
                userId,
                null,
                configuration,
                clients,
                ct);
            if (result.StatusCode != StatusCodes.Status200OK)
                return Results.Content(result.Body, "application/json", Encoding.UTF8, result.StatusCode);

            using var doc = JsonDocument.Parse(result.Body);
            var status = doc.RootElement.TryGetProperty("status", out var statusNode)
                ? statusNode.GetString()
                : null;
            if (string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase))
            {
                var existing = await db.AiCredentials
                    .SingleOrDefaultAsync(x =>
                        x.OwnerUserId == userId &&
                        x.Provider == IntelligenceProviders.Codex, ct);
                var scope = CodexBridgeIntelligenceProvider.ScopeForUser(userId);
                AiCredentialView view;
                if (existing is null)
                {
                    view = await store.CreateCredentialAsync(
                        userId,
                        IntelligenceProviders.Codex,
                        "Codex / ChatGPT Login",
                        scope,
                        ct);
                }
                else
                {
                    view = new(
                        existing.Id,
                        existing.OwnerUserId,
                        existing.Provider,
                        existing.Name,
                        existing.SecretFingerprint,
                        existing.CreatedAt,
                        existing.UpdatedAt,
                        existing.LastTestedAt,
                        existing.LastTestSucceeded);
                }

                await store.SelectUserCredentialAsync(userId, view.Id, null, null, ct);
                await store.DeleteOtherUserCredentialsAsync(userId, view.Id, ct);
            }

            return Results.Content(result.Body, "application/json", Encoding.UTF8, result.StatusCode);
        });

        group.MapPost("/codex/logout", async (
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            IntelligenceStore store,
            IConfiguration configuration,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var selectedProvider = await SelectedProviderAsync(userId, db, ct);
            var result = await ForwardCodexJsonAsync(
                HttpMethod.Post,
                "/logout",
                userId,
                "{}",
                configuration,
                clients,
                ct);
            if (selectedProvider == IntelligenceProviders.Codex)
                await store.ClearUserAccessAsync(userId, ct);
            return Results.Content(result.Body, "application/json", Encoding.UTF8, result.StatusCode);
        });

        group.MapPost("/test", async (
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            IntelligenceStore store,
            IntelligenceProviderRegistry providers,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var settings = await db.AiUserSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == userId, ct);
            if (settings?.CredentialId is not { } credentialId)
                return Results.NotFound(new { error = "ai_access_not_configured" });

            var credential = await db.AiCredentials.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == credentialId && x.OwnerUserId == userId, ct);
            if (credential is null)
                return Results.NotFound(new { error = "ai_access_not_configured" });

            var secret = await store.ResolveCredentialSecretAsync(credential.Id, userId, ct);
            var test = await providers.GetRequired(credential.Provider).TestCredentialAsync(secret, ct);
            return test.Success ? Results.Ok(test) : Results.UnprocessableEntity(test);
        });

        group.MapDelete("/", async (
            CurrentUserContext currentUser,
            IntelligenceDbContext db,
            IntelligenceStore store,
            IConfiguration configuration,
            IHttpClientFactory clients,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (await SelectedProviderAsync(userId, db, ct) == IntelligenceProviders.Codex)
            {
                try
                {
                    await ForwardCodexJsonAsync(
                        HttpMethod.Post,
                        "/logout",
                        userId,
                        "{}",
                        configuration,
                        clients,
                        ct);
                }
                catch
                {
                    // Local deletion must remain possible even when the sidecar is unavailable.
                }
            }
            await store.ClearUserAccessAsync(userId, ct);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task<string?> SelectedProviderAsync(
        Guid userId,
        IntelligenceDbContext db,
        CancellationToken ct)
    {
        var credentialId = await db.AiUserSettings.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.CredentialId)
            .SingleOrDefaultAsync(ct);
        if (!credentialId.HasValue) return null;
        return await db.AiCredentials.AsNoTracking()
            .Where(x => x.Id == credentialId.Value && x.OwnerUserId == userId)
            .Select(x => x.Provider)
            .SingleOrDefaultAsync(ct);
    }

    private static async Task<bool> TryGetCodexConnectedAsync(
        Guid userId,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        try
        {
            var result = await ForwardCodexJsonAsync(
                HttpMethod.Get,
                "/status",
                userId,
                null,
                configuration,
                clients,
                ct);
            if (result.StatusCode != StatusCodes.Status200OK) return false;
            using var doc = JsonDocument.Parse(result.Body);
            return doc.RootElement.TryGetProperty("connected", out var connected) &&
                   connected.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<IResult> ForwardCodexAsync(
        HttpMethod method,
        string path,
        Guid userId,
        string? json,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        var result = await ForwardCodexJsonAsync(
            method,
            path,
            userId,
            json,
            configuration,
            clients,
            ct);
        return Results.Content(result.Body, "application/json", Encoding.UTF8, result.StatusCode);
    }

    private static async Task<(int StatusCode, string Body)> ForwardCodexJsonAsync(
        HttpMethod method,
        string path,
        Guid userId,
        string? json,
        IConfiguration configuration,
        IHttpClientFactory clients,
        CancellationToken ct)
    {
        var baseUrl = (configuration["AiAccess:CodexBridgeBaseUrl"] ??
                       configuration["CodexTest:BaseUrl"] ??
                       "http://fullworth-codex:8080").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttp)
            return (StatusCodes.Status503ServiceUnavailable, "{"error":"codex_bridge_invalid"}");

        var key = configuration["AiAccess:CodexBridgeKey"] ??
                  configuration["CodexTest:BridgeKey"];
        if (string.IsNullOrWhiteSpace(key))
            return (StatusCodes.Status503ServiceUnavailable, "{"error":"codex_bridge_unavailable"}");

        using var message = new HttpRequestMessage(method, new Uri(baseUri, path));
        message.Headers.Add("X-FullWorth-Internal-Key", key);
        message.Headers.Add(BridgeScopeHeader, CodexBridgeIntelligenceProvider.ScopeForUser(userId));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var client = clients.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(11);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return ((int)response.StatusCode, string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (StatusCodes.Status504GatewayTimeout, "{"error":"codex_bridge_timeout"}");
        }
        catch (HttpRequestException)
        {
            return (StatusCodes.Status503ServiceUnavailable, "{"error":"codex_bridge_unavailable"}");
        }
    }
}
