using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FullWorth.Banking.Backend;

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

/// <summary>
/// Reads Enable Banking's Control Panel ASPSP health feed. The endpoint is the same
/// /api/get_today_stats endpoint used by Enable Banking's official CLI `aspsp status` command.
/// Only a short-lived ID token is kept in memory. The long-lived refresh token is stored encrypted
/// in the backend profile and is never returned to the browser.
/// </summary>
public sealed class EnableBankingControlPanelStatusService(
    IHttpClientFactory httpClientFactory,
    FullWorthBackendClient backend,
    ILogger<EnableBankingControlPanelStatusService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, CachedToken> _tokenCache = new();

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

        var cachedToken = new CachedToken(
            refreshed.IdToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(refreshed.ExpiresInSeconds - 90, 300, 3300)));
        _tokenCache[userId] = cachedToken;

        if (!string.Equals(
                refreshed.RefreshToken,
                profile.ControlPanelRefreshToken,
                StringComparison.Ordinal))
        {
            await backend.UpsertEnableBankingProfileAsync(new(
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
                refreshed.RefreshToken), ct);
        }

        return cachedToken;
    }

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
}
