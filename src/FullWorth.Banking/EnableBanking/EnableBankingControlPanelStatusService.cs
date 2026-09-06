using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Banking.Backend;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.EnableBanking;

public sealed record EnableBankingAspspStatusView(
    string Country,
    string Brand,
    string PsuType,
    string Status);

public sealed record EnableBankingProviderStatusView(
    bool Available,
    string? Reason,
    DateTimeOffset CheckedAt,
    IReadOnlyList<EnableBankingAspspStatusView> Statuses);

public sealed record EnableBankingProviderStatusConnectRequest(string Email);

public sealed record EnableBankingProviderStatusConnectStart(
    string Id,
    string Status,
    string SetupCallbackUrl,
    bool ManualCompletionRequired);

public sealed record EnableBankingProviderStatusConnectCompleteRequest(
    string Id,
    string LoginLinkOrCode);

public sealed record EnableBankingProviderStatusConnectCallbackResult(
    bool Success,
    string Status,
    string? ErrorCode);

/// <summary>
/// Reads Enable Banking's Control Panel ASPSP health feed. The endpoint is the same
/// /api/get_today_stats endpoint used by Enable Banking's official CLI `aspsp status` command.
/// Only a short-lived ID token is kept in memory. The long-lived refresh token is stored encrypted
/// in the backend profile and is never returned to the browser.
/// </summary>
public sealed class EnableBankingControlPanelStatusService(
    IHttpClientFactory httpClientFactory,
    FullWorthBackendClient backend,
    IOptions<EnableBankingOptions> options,
    ILogger<EnableBankingControlPanelStatusService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, CachedToken> _tokenCache = new();
    private readonly ConcurrentDictionary<string, PendingStatusConnection> _pendingConnections = new(StringComparer.Ordinal);
    private readonly EnableBankingOptions _options = options.Value;

    public async Task<EnableBankingProviderStatusConnectStart> StartConnectionAsync(
        Guid userId,
        EnableBankingProviderStatusConnectRequest request,
        CancellationToken ct)
    {
        PrunePendingConnections();

        if (userId == Guid.Empty)
            throw new ArgumentException("A FullWorth user is required.");

        var profile = await backend.GetEnableBankingProfileForUserAsync(userId, ct);
        if (profile is null)
            throw new InvalidOperationException("Enable Banking must be configured first.");

        var email = (request.Email ?? string.Empty).Trim();
        if (email.Length is < 3 or > 254 || !MailAddress.TryCreate(email, out _))
            throw new ArgumentException("Enter a valid email address.");

        if (string.IsNullOrWhiteSpace(_options.RedirectUrl) ||
            !Uri.TryCreate(_options.RedirectUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("EnableBanking:RedirectUrl is not configured with an absolute URL.");

        var id = RandomToken(32);
        var setupCallbackUrl = BuildStatusCallbackUrl(id);
        var pending = new PendingStatusConnection
        {
            Id = id,
            UserId = userId,
            Email = email,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20)
        };
        if (!_pendingConnections.TryAdd(id, pending))
            throw new InvalidOperationException("Unable to create Enable Banking status setup state.");

        var manualCompletionRequired = false;
        try
        {
            var response = await StartEmailSignInAsync(email, setupCallbackUrl, ct);
            if (!IsSuccess(response.StatusCode) && IsContinueUrlRejected(response.Body))
            {
                // Enable Banking's Control Panel authentication is backed by a Firebase email-link
                // flow. Self-hosted FullWorth domains are not necessarily allow-listed there.
                // Fall back to the same loopback continue URL used by Enable Banking's official CLI;
                // the browser can then paste the resulting localhost URL back into FullWorth.
                setupCallbackUrl = "http://localhost:8888/";
                manualCompletionRequired = true;
                response = await StartEmailSignInAsync(email, setupCallbackUrl, ct);
            }

            if (!IsSuccess(response.StatusCode))
            {
                var safeMessage = SafeLoginStartMessage(response.Body);
                logger.LogWarning(
                    "Enable Banking Control Panel sign-in start returned {StatusCode} ({Reason}).",
                    (int)response.StatusCode,
                    SafeControlPanelReason(response.Body));
                throw new InvalidOperationException(safeMessage);
            }
        }
        catch
        {
            _pendingConnections.TryRemove(id, out _);
            throw;
        }

        return new(id, "waiting_for_email", setupCallbackUrl, manualCompletionRequired);
    }

    public async Task<EnableBankingProviderStatusConnectCallbackResult> CompleteConnectionManuallyAsync(
        Guid userId,
        EnableBankingProviderStatusConnectCompleteRequest request,
        CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A FullWorth user is required.");
        if (string.IsNullOrWhiteSpace(request.Id) ||
            !_pendingConnections.TryGetValue(request.Id, out var pending) ||
            pending.UserId != userId)
            return new(false, "expired", "setup_expired");

        var oobCode = ExtractOobCode(request.LoginLinkOrCode);
        if (string.IsNullOrWhiteSpace(oobCode))
            return new(false, "waiting_for_email", "missing_oob_code");

        return await CompleteConnectionAsync(request.Id, oobCode, ct);
    }

    public async Task<EnableBankingProviderStatusConnectCallbackResult> CompleteConnectionAsync(
        string? id,
        string? oobCode,
        CancellationToken ct)
    {
        PrunePendingConnections();
        if (string.IsNullOrWhiteSpace(id) ||
            !_pendingConnections.TryGetValue(id, out var pending) ||
            pending.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(false, "expired", "setup_expired");

        if (string.IsNullOrWhiteSpace(oobCode))
            return new(false, "waiting_for_email", "missing_oob_code");

        if (Interlocked.CompareExchange(ref pending.Claimed, 1, 0) != 0)
            return new(false, "processing", "setup_in_progress");

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                oobCode,
                email = pending.Email
            }, JsonOptions);
            var signIn = await SendControlPanelAsync(
                HttpMethod.Post,
                "/api/relyingparty/emailLinkSignin",
                new StringContent(body, Encoding.UTF8, "application/json"),
                bearerToken: null,
                ct);
            if ((int)signIn.StatusCode is < 200 or > 299)
                return new(false, "failed", "control_panel_login_failed");

            using var document = JsonDocument.Parse(signIn.Body);
            var refreshToken = GetString(document.RootElement, "refreshToken");
            if (string.IsNullOrWhiteSpace(refreshToken))
                return new(false, "failed", "control_panel_token_missing");

            var profile = await backend.GetEnableBankingProfileForUserAsync(pending.UserId, ct);
            if (profile is null)
                return new(false, "failed", "banking_profile_missing");

            await PersistRefreshTokenAsync(profile, refreshToken, ct);
            _tokenCache.TryRemove(pending.UserId, out _);
            _pendingConnections.TryRemove(id, out _);
            pending.Email = string.Empty;
            return new(true, "completed", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Enable Banking bank-status sign-in failed.");
            return new(false, "failed", "control_panel_login_failed");
        }
        finally
        {
            Volatile.Write(ref pending.Claimed, 0);
        }
    }

    public async Task<EnableBankingProviderStatusView> GetTodayAsync(
        Guid userId,
        string? country,
        CancellationToken ct)
    {
        var normalizedCountry = NormalizeCountry(country);
        var profile = await backend.GetEnableBankingProfileForUserAsync(userId, ct);
        if (profile is null || string.IsNullOrWhiteSpace(profile.ControlPanelRefreshToken))
            return Unavailable("control_panel_access_unavailable");

        var token = await GetTokenAsync(userId, profile, forceRefresh: false, ct);
        if (token is null)
            return Unavailable("control_panel_login_expired");

        var response = await FetchTodayStatsAsync(token.IdToken, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _tokenCache.TryRemove(userId, out _);
            profile = await backend.GetEnableBankingProfileForUserAsync(userId, ct);
            if (profile is null || string.IsNullOrWhiteSpace(profile.ControlPanelRefreshToken))
                return Unavailable("control_panel_access_unavailable");

            token = await GetTokenAsync(userId, profile, forceRefresh: true, ct);
            if (token is null)
                return Unavailable("control_panel_login_expired");

            response = await FetchTodayStatsAsync(token.IdToken, ct);
        }

        if ((int)response.StatusCode is < 200 or > 299)
        {
            logger.LogWarning(
                "Enable Banking ASPSP status API returned {StatusCode}.",
                (int)response.StatusCode);
            return Unavailable("provider_status_unavailable");
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Unavailable("provider_status_invalid");

            var statuses = new List<EnableBankingAspspStatusView>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var itemCountry = GetString(item, "country")?.Trim().ToUpperInvariant();
                var brand = GetString(item, "brand")?.Trim();
                var psuType = GetString(item, "psu_type")?.Trim().ToLowerInvariant();
                var status = GetString(item, "status")?.Trim();

                if (string.IsNullOrWhiteSpace(itemCountry) ||
                    string.IsNullOrWhiteSpace(brand) ||
                    string.IsNullOrWhiteSpace(status))
                    continue;
                if (normalizedCountry is not null &&
                    !string.Equals(itemCountry, normalizedCountry, StringComparison.OrdinalIgnoreCase))
                    continue;

                statuses.Add(new(
                    itemCountry,
                    brand,
                    string.IsNullOrWhiteSpace(psuType) ? "personal" : psuType,
                    status));
            }

            return new(true, null, DateTimeOffset.UtcNow, statuses);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Enable Banking ASPSP status response was invalid JSON.");
            return Unavailable("provider_status_invalid");
        }
    }

    private async Task<CachedToken?> GetTokenAsync(
        Guid userId,
        EnableBankingProfileDto profile,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (!forceRefresh &&
            _tokenCache.TryGetValue(userId, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return cached;

        var refreshed = await RefreshAsync(profile.ControlPanelRefreshToken!, ct);
        if (refreshed is null)
            return null;

        var cacheSeconds = Math.Max(30, Math.Min(refreshed.ExpiresInSeconds - 90, 3300));
        var cachedToken = new CachedToken(
            refreshed.IdToken,
            DateTimeOffset.UtcNow.AddSeconds(cacheSeconds));
        _tokenCache[userId] = cachedToken;

        if (!string.Equals(
                refreshed.RefreshToken,
                profile.ControlPanelRefreshToken,
                StringComparison.Ordinal))
        {
            await PersistRefreshTokenAsync(profile, refreshed.RefreshToken, ct);
        }

        return cachedToken;
    }

    private Task<EnableBankingProfileDto> PersistRefreshTokenAsync(
        EnableBankingProfileDto profile,
        string refreshToken,
        CancellationToken ct) =>
        backend.UpsertEnableBankingProfileAsync(new(
            profile.UserId,
            profile.ApplicationId,
            profile.PrivateKeyPem,
            profile.KeyFingerprint,
            profile.Environment,
            profile.ApplicationName,
            profile.Active,
            profile.Services,
            profile.RedirectUrls,
            profile.VerifiedAt ?? DateTimeOffset.UtcNow,
            refreshToken), ct);

    private async Task<RefreshedToken?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("enable-banking-control-panel");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Enable Banking Control Panel token refresh returned {StatusCode}.",
                (int)response.StatusCode);
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var idToken = GetString(document.RootElement, "id_token");
            if (string.IsNullOrWhiteSpace(idToken))
                return null;

            var rotatedRefreshToken =
                GetString(document.RootElement, "refresh_token") ?? refreshToken;
            var expiresIn = GetInt(document.RootElement, "expires_in") ?? 3600;
            return new(idToken, rotatedRefreshToken, expiresIn);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ControlPanelResponse> FetchTodayStatsAsync(string idToken, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("enable-banking-control-panel");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/get_today_stats");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return new(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private Task<ControlPanelResponse> StartEmailSignInAsync(
        string email,
        string continueUrl,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            requestType = "EMAIL_SIGNIN",
            email,
            continueUrl,
            canHandleCodeInApp = true
        }, JsonOptions);

        return SendControlPanelAsync(
            HttpMethod.Post,
            "/api/relyingparty/getOobConfirmationCode",
            new StringContent(body, Encoding.UTF8, "application/json"),
            bearerToken: null,
            ct);
    }

    private async Task<ControlPanelResponse> SendControlPanelAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        string? bearerToken,
        CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("enable-banking-control-panel");
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return new(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static bool IsSuccess(HttpStatusCode statusCode) =>
        (int)statusCode is >= 200 and <= 299;

    private static bool IsContinueUrlRejected(string body)
    {
        var reason = SafeControlPanelReason(body);
        return reason is
            "INVALID_CONTINUE_URI" or
            "UNAUTHORIZED_CONTINUE_URI" or
            "UNAUTHORIZED_DOMAIN" or
            "INVALID_DYNAMIC_LINK_DOMAIN";
    }

    private static string SafeLoginStartMessage(string body)
    {
        var reason = SafeControlPanelReason(body);
        return reason switch
        {
            "EMAIL_NOT_FOUND" or "USER_NOT_FOUND" =>
                "No Enable Banking account exists for this email address.",
            "TOO_MANY_ATTEMPTS_TRY_LATER" or "TOO_MANY_REQUESTS" =>
                "Enable Banking temporarily blocked sign-in requests. Try again later.",
            "INVALID_CONTINUE_URI" or
            "UNAUTHORIZED_CONTINUE_URI" or
            "UNAUTHORIZED_DOMAIN" or
            "INVALID_DYNAMIC_LINK_DOMAIN" =>
                "Enable Banking rejected the sign-in callback.",
            _ => "Enable Banking could not start the Control Panel sign-in."
        };
    }

    private static string SafeControlPanelReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "UNKNOWN";

        string? providerMessage = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    providerMessage = error.GetString();
                else if (error.ValueKind == JsonValueKind.Object &&
                         error.TryGetProperty("message", out var nested) &&
                         nested.ValueKind == JsonValueKind.String)
                    providerMessage = nested.GetString();
            }

            if (providerMessage is null &&
                root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
                providerMessage = message.GetString();
        }
        catch (JsonException)
        {
            // Fall through to the same allow-listed scan for non-JSON provider responses.
        }

        var normalized = NormalizeReason(providerMessage ?? body);
        foreach (var known in new[]
                 {
                     "INVALID_CONTINUE_URI",
                     "UNAUTHORIZED_CONTINUE_URI",
                     "UNAUTHORIZED_DOMAIN",
                     "INVALID_DYNAMIC_LINK_DOMAIN",
                     "EMAIL_NOT_FOUND",
                     "USER_NOT_FOUND",
                     "TOO_MANY_ATTEMPTS_TRY_LATER",
                     "TOO_MANY_REQUESTS"
                 })
            if (normalized.Contains(known, StringComparison.Ordinal))
                return known;

        return "UNKNOWN";
    }

    private static string NormalizeReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "UNKNOWN";
        var normalized = new string(value
            .Trim()
            .ToUpperInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
            .Take(240)
            .ToArray());
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        return normalized.Trim('_');
    }

    private static string? ExtractOobCode(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length is < 4 or > 8192) return null;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return raw.Contains(' ') ? null : raw;

        var direct = QueryValue(uri.Query, "oobCode") ?? QueryValue(uri.Fragment, "oobCode");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;

        var nested = QueryValue(uri.Query, "link");
        if (!string.IsNullOrWhiteSpace(nested) &&
            Uri.TryCreate(Uri.UnescapeDataString(nested), UriKind.Absolute, out var nestedUri))
            return QueryValue(nestedUri.Query, "oobCode") ?? QueryValue(nestedUri.Fragment, "oobCode");

        return null;
    }

    private static string? QueryValue(string queryOrFragment, string key)
    {
        var query = queryOrFragment.TrimStart('?', '#');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var rawKey = separator < 0 ? part : part[..separator];
            if (!string.Equals(Uri.UnescapeDataString(rawKey.Replace("+", " ", StringComparison.Ordinal)), key, StringComparison.OrdinalIgnoreCase))
                continue;
            var rawValue = separator < 0 ? string.Empty : part[(separator + 1)..];
            return Uri.UnescapeDataString(rawValue.Replace("+", " ", StringComparison.Ordinal));
        }
        return null;
    }

    private string BuildStatusCallbackUrl(string id)
    {
        var redirect = new Uri(_options.RedirectUrl, UriKind.Absolute);
        var builder = new UriBuilder(redirect)
        {
            Path = "/connect/enable-banking/status-callback",
            Query = $"state={Uri.EscapeDataString(id)}",
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    private void PrunePendingConnections()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _pendingConnections)
        {
            if (pair.Value.ExpiresAt <= now && _pendingConnections.TryRemove(pair.Key, out var removed))
                removed.Email = string.Empty;
        }
    }

    private static string RandomToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string? NormalizeCountry(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var value = country.Trim().ToUpperInvariant();
        if (value.Length != 2 || value.Any(ch => ch is < 'A' or > 'Z'))
            throw new ArgumentException("Country must be a two-letter ISO code.");
        return value;
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)) return number;
        return null;
    }

    private static EnableBankingProviderStatusView Unavailable(string reason) =>
        new(false, reason, DateTimeOffset.UtcNow, []);

    private sealed record CachedToken(string IdToken, DateTimeOffset ExpiresAt);
    private sealed record RefreshedToken(string IdToken, string RefreshToken, int ExpiresInSeconds);
    private sealed record ControlPanelResponse(HttpStatusCode StatusCode, string Body);

    private sealed class PendingStatusConnection
    {
        public required string Id { get; init; }
        public Guid UserId { get; init; }
        public string Email { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; init; }
        public int Claimed;
    }
}
