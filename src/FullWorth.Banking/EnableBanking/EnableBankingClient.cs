using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.EnableBanking;

public sealed class EnableBankingOptions
{
    public const string SectionName = "EnableBanking";
    public string BaseUrl { get; set; } = "https://api.enablebanking.com";
    public string ApplicationId { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = "/run/secrets/enable-banking-private-key.pem";
    public string DefaultCountry { get; set; } = "DE";
    public string DefaultPsuType { get; set; } = "personal";
    // The OAuth redirect target is derived from server configuration, never from the browser (P0.2).
    public string RedirectUrl { get; set; } = string.Empty;
    // Authorization-state time-to-live; the callback must arrive within this window.
    public int AuthorizationStateTtlMinutes { get; set; } = 15;
    public int MinimumRequestSpacingMilliseconds { get; set; } = 1000;
    public int TransientRetryCount { get; set; } = 2;
}

public sealed record StartAuthorizationResult(string Url, string AuthorizationId, string? PsuIdHash);

public sealed class EnableBankingClient(
    HttpClient http,
    IOptions<EnableBankingOptions> options,
    EnableBankingRequestPolicy requestPolicy)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly EnableBankingOptions _options = options.Value;

    public Task<JsonElement> GetApplicationAsync(CancellationToken ct) => SendJsonAsync(HttpMethod.Get, "/application", null, ct);
    public Task<JsonElement> GetInstitutionsAsync(string country, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, $"/aspsps?country={Uri.EscapeDataString(country)}&psu_type={Uri.EscapeDataString(_options.DefaultPsuType)}&service=AIS", null, ct);
    public Task<JsonElement> GetSessionAsync(string sessionId, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, $"/sessions/{Uri.EscapeDataString(sessionId)}", null, ct);
    public Task<JsonElement> GetAccountAsync(string accountId, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(accountId)}", null, ct);
    public Task<JsonElement> GetBalancesAsync(string accountId, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(accountId)}/balances", null, ct);
    public Task<JsonElement> AuthorizeSessionAsync(string code, CancellationToken ct) => SendJsonAsync(HttpMethod.Post, "/sessions", new { code }, ct);

    public Task<JsonElement> GetTransactionsAsync(string accountId, DateOnly? from, DateOnly? to, bool initialSync, string? continuationKey, CancellationToken ct)
    {
        var strategy = initialSync ? "longest" : "default";
        var path = $"/accounts/{Uri.EscapeDataString(accountId)}/transactions?strategy={strategy}";

        // For the first import Enable Banking's "longest" strategy is intentionally called without
        // date_from/date_to so the provider can discover the earliest history the ASPSP exposes.
        if (!initialSync)
        {
            if (from.HasValue) path += $"&date_from={from.Value:yyyy-MM-dd}";
            if (to.HasValue) path += $"&date_to={to.Value:yyyy-MM-dd}";
        }

        if (!string.IsNullOrWhiteSpace(continuationKey))
            path += $"&continuation_key={Uri.EscapeDataString(continuationKey)}";
        return SendJsonAsync(HttpMethod.Get, path, null, ct);
    }

    public async Task<StartAuthorizationResult> StartAuthorizationAsync(
        string institutionName,
        string country,
        string redirectUrl,
        string state,
        DateTimeOffset validUntil,
        string? authMethod,
        string? psuId,
        IReadOnlyDictionary<string, string>? credentials,
        CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["access"] = new { balances = true, transactions = true, valid_until = validUntil },
            ["aspsp"] = new { name = institutionName, country },
            ["state"] = state,
            ["redirect_url"] = redirectUrl,
            ["psu_type"] = _options.DefaultPsuType
        };
        if (!string.IsNullOrWhiteSpace(authMethod)) body["auth_method"] = authMethod;
        if (!string.IsNullOrWhiteSpace(psuId)) body["psu_id"] = psuId;
        if (credentials is { Count: > 0 }) body["credentials"] = credentials;

        var json = await SendJsonAsync(HttpMethod.Post, "/auth", body, ct);
        var url = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Enable Banking /auth did not return an authorization url.");
        return new(
            url,
            json.TryGetProperty("authorization_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
            json.TryGetProperty("psu_id_hash", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null);
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        EnsureConfigured();
        var serializedBody = body is null ? null : JsonSerializer.Serialize(body, JsonOptions);
        var retries = Math.Clamp(_options.TransientRetryCount, 0, 3);

        for (var attempt = 0; ; attempt++)
        {
            using var lease = await requestPolicy.EnterAsync(
                TimeSpan.FromMilliseconds(Math.Clamp(_options.MinimumRequestSpacingMilliseconds, 250, 10000)), ct);

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (serializedBody is not null)
                request.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(content);
                return doc.RootElement.Clone();
            }

            var errorCode = TryGetErrorCode(content);
            var retryAt = GetRetryAt(response);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new EnableBankingApiException(response.StatusCode, errorCode, content, retryAt);

            if (!IsTransient(response.StatusCode) || attempt >= retries)
                throw new EnableBankingApiException(response.StatusCode, errorCode, content, retryAt);

            var delay = retryAt.HasValue
                ? retryAt.Value - DateTimeOffset.UtcNow
                : TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble());
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
            await Task.Delay(delay, ct);
        }
    }

    private static bool IsTransient(HttpStatusCode code) => code is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static DateTimeOffset? GetRetryAt(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is not null) return retry.Date;
        if (retry?.Delta is not null) return DateTimeOffset.UtcNow.Add(retry.Delta.Value);
        return null;
    }

    private static string? TryGetErrorCode(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            foreach (var name in new[] { "error_code", "code", "error" })
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private string CreateJwt()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["typ"] = "JWT",
            ["alg"] = "RS256",
            ["kid"] = _options.ApplicationId
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = "enablebanking.com",
            ["aud"] = "api.enablebanking.com",
            ["iat"] = now,
            ["exp"] = now + 3600
        });
        var unsigned = $"{B64(header)}.{B64(payload)}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(_options.PrivateKeyPath));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{B64(signature)}";
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ApplicationId))
            throw new InvalidOperationException("EnableBanking:ApplicationId is not configured.");
        if (!File.Exists(_options.PrivateKeyPath))
            throw new InvalidOperationException($"Enable Banking private key not found at '{_options.PrivateKeyPath}'.");
    }

    private static string B64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
