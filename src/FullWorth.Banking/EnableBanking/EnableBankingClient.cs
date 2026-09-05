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
    // Optional legacy/global key for container deployments that do not mount a PEM file.
    // New BYO profiles store their own encrypted PEM in FullWorth.Backend and do not use this value.
    public string PrivateKeyBase64 { get; set; } = string.Empty;
    public string DefaultCountry { get; set; } = "DE";
    public string DefaultPsuType { get; set; } = "personal";
    // Callback URL is server configuration and is never accepted from a browser connect request.
    public string RedirectUrl { get; set; } = string.Empty;
    public int AuthorizationStateTtlMinutes { get; set; } = 15;
    public int MinimumRequestSpacingMilliseconds { get; set; } = 1000;
    public int TransientRetryCount { get; set; } = 2;
}

public sealed record EnableBankingCredentials(string ApplicationId, string PrivateKeyPem);
public sealed record StartAuthorizationResult(string Url, string AuthorizationId, string? PsuIdHash);

/// <summary>
/// Trusted browser-presence context. These are the documented Enable Banking PSU headers only.
/// A provider request either sends every ASPSP-required PSU header or none, never a partial set.
/// </summary>
public sealed class PsuContext
{
    private static readonly string[] AllowedHeaders =
    [
        "Psu-Ip-Address",
        "Psu-User-Agent",
        "Psu-Referer",
        "Psu-Accept",
        "Psu-Accept-Charset",
        "Psu-Accept-Encoding",
        "Psu-Accept-language",
        "Psu-Geo-Location"
    ];

    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public PsuContext(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var (name, value) in headers)
            if (AllowedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                _headers[name] = value.Trim();
    }

    public bool IsEmpty => _headers.Count == 0;

    public IReadOnlyDictionary<string, string> HeadersFor(IReadOnlyCollection<string>? requiredHeaders)
    {
        if (IsEmpty) return new Dictionary<string, string>();

        var required = requiredHeaders?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        // Enable Banking documents PSU_HEADER_NOT_PROVIDED when only part of the bank-required set is
        // supplied. Falling back to zero PSU headers makes the request an explicit background fetch.
        if (required.Any(name => !_headers.ContainsKey(name)))
            return new Dictionary<string, string>();

        return new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class EnableBankingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;
    private readonly EnableBankingOptions _options;
    private readonly EnableBankingRequestPolicy _requestPolicy;
    private readonly EnableBankingCredentials? _credentials;

    // Legacy/global DI constructor.
    public EnableBankingClient(
        HttpClient http,
        IOptions<EnableBankingOptions> options,
        EnableBankingRequestPolicy requestPolicy)
        : this(http, options, requestPolicy, null)
    {
    }

    // Per-user BYO Enable Banking constructor.
    public EnableBankingClient(
        HttpClient http,
        IOptions<EnableBankingOptions> options,
        EnableBankingRequestPolicy requestPolicy,
        EnableBankingCredentials? credentials)
    {
        _http = http;
        _options = options.Value;
        _requestPolicy = requestPolicy;
        _credentials = credentials;
    }

    public string ApplicationId => _credentials?.ApplicationId ?? _options.ApplicationId;

    public Task<JsonElement> GetApplicationAsync(CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Get, "/application", null, null, null, ct);

    public Task<JsonElement> GetInstitutionsAsync(string country, CancellationToken ct) =>
        GetInstitutionsAsync(country, _options.DefaultPsuType, ct);

    public Task<JsonElement> GetInstitutionsAsync(string country, string? psuType, CancellationToken ct)
    {
        var path = $"/aspsps?country={Uri.EscapeDataString(country)}&service=AIS";
        if (!string.IsNullOrWhiteSpace(psuType))
            path += $"&psu_type={Uri.EscapeDataString(psuType)}";
        return SendJsonAsync(HttpMethod.Get, path, null, null, null, ct);
    }

    public Task<JsonElement> GetSessionAsync(string sessionId, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Get, $"/sessions/{Uri.EscapeDataString(sessionId)}", null, null, null, ct);

    public Task<JsonElement> DeleteSessionAsync(
        string sessionId,
        PsuContext? psuContext,
        IReadOnlyCollection<string>? requiredPsuHeaders,
        CancellationToken ct) =>
        SendJsonAsync(
            HttpMethod.Delete,
            $"/sessions/{Uri.EscapeDataString(sessionId)}",
            null,
            psuContext,
            requiredPsuHeaders,
            ct);

    public Task<JsonElement> DeleteSessionAsync(string sessionId, PsuContext? psuContext, CancellationToken ct) =>
        DeleteSessionAsync(sessionId, psuContext, null, ct);

    public Task<JsonElement> GetAccountAsync(
        string accountId,
        PsuContext? psuContext,
        IReadOnlyCollection<string>? requiredPsuHeaders,
        CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(accountId)}/details", null, psuContext, requiredPsuHeaders, ct);

    public Task<JsonElement> GetAccountAsync(string accountId, CancellationToken ct) =>
        GetAccountAsync(accountId, null, null, ct);

    public Task<JsonElement> GetBalancesAsync(
        string accountId,
        PsuContext? psuContext,
        IReadOnlyCollection<string>? requiredPsuHeaders,
        CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Get, $"/accounts/{Uri.EscapeDataString(accountId)}/balances", null, psuContext, requiredPsuHeaders, ct);

    public Task<JsonElement> GetBalancesAsync(string accountId, CancellationToken ct) =>
        GetBalancesAsync(accountId, null, null, ct);

    public Task<JsonElement> AuthorizeSessionAsync(string code, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Post, "/sessions", new { code }, null, null, ct);

    public Task<JsonElement> GetTransactionDetailsAsync(
        string accountId,
        string transactionId,
        PsuContext? psuContext,
        IReadOnlyCollection<string>? requiredPsuHeaders,
        CancellationToken ct) =>
        SendJsonAsync(
            HttpMethod.Get,
            $"/accounts/{Uri.EscapeDataString(accountId)}/transactions/{Uri.EscapeDataString(transactionId)}",
            null,
            psuContext,
            requiredPsuHeaders,
            ct);

    public Task<JsonElement> GetTransactionsAsync(
        string accountId,
        DateOnly? from,
        DateOnly? to,
        bool initialSync,
        string? continuationKey,
        CancellationToken ct) =>
        GetTransactionsAsync(accountId, from, to, initialSync, continuationKey, null, null, ct);

    public Task<JsonElement> GetTransactionsAsync(
        string accountId,
        DateOnly? from,
        DateOnly? to,
        bool initialSync,
        string? continuationKey,
        PsuContext? psuContext,
        IReadOnlyCollection<string>? requiredPsuHeaders,
        CancellationToken ct)
    {
        var strategy = initialSync ? "longest" : "default";
        var path = $"/accounts/{Uri.EscapeDataString(accountId)}/transactions?strategy={strategy}";

        // Enable Banking explicitly recommends longest without date_from for first retrieval so it can
        // discover the earliest history available from the ASPSP. date_to is ignored by longest.
        if (!initialSync)
        {
            if (from.HasValue) path += $"&date_from={from.Value:yyyy-MM-dd}";
            if (to.HasValue) path += $"&date_to={to.Value:yyyy-MM-dd}";
        }

        if (!string.IsNullOrWhiteSpace(continuationKey))
            path += $"&continuation_key={Uri.EscapeDataString(continuationKey)}";

        return SendJsonAsync(HttpMethod.Get, path, null, psuContext, requiredPsuHeaders, ct);
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
        CancellationToken ct,
        string? psuType = null,
        string? language = null,
        bool? credentialsAutosubmit = null)
    {
        if (credentials is { Count: > 0 } && string.IsNullOrWhiteSpace(authMethod))
            throw new ArgumentException("Enable Banking credentials may only be supplied together with auth_method.");

        var body = new Dictionary<string, object?>
        {
            ["access"] = new { balances = true, transactions = true, valid_until = validUntil },
            ["aspsp"] = new { name = institutionName, country },
            ["state"] = state,
            ["redirect_url"] = redirectUrl,
            ["psu_type"] = string.IsNullOrWhiteSpace(psuType) ? _options.DefaultPsuType : psuType
        };
        if (!string.IsNullOrWhiteSpace(authMethod)) body["auth_method"] = authMethod;
        if (!string.IsNullOrWhiteSpace(psuId)) body["psu_id"] = psuId;
        if (!string.IsNullOrWhiteSpace(language)) body["language"] = language.ToLowerInvariant();
        if (credentials is { Count: > 0 })
        {
            body["credentials"] = credentials;
            if (credentialsAutosubmit.HasValue) body["credentials_autosubmit"] = credentialsAutosubmit.Value;
        }

        var json = await SendJsonAsync(HttpMethod.Post, "/auth", body, null, null, ct);
        var url = json.ValueKind == JsonValueKind.Object &&
                  json.TryGetProperty("url", out var urlElement) &&
                  urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Enable Banking /auth did not return an authorization url.");

        return new(
            url,
            json.TryGetProperty("authorization_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "",
            json.TryGetProperty("psu_id_hash", out var hash) && hash.ValueKind == JsonValueKind.String ? hash.GetString() : null);
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string path,
        object? body,
        PsuContext? psuContext,
        IReadOnlyCollection<string>? requiredPsuHeaders,
        CancellationToken ct)
    {
        EnsureConfigured();
        var serializedBody = body is null ? null : JsonSerializer.Serialize(body, JsonOptions);
        var retries = Math.Clamp(_options.TransientRetryCount, 0, 3);

        for (var attempt = 0; ; attempt++)
        {
            using var lease = await _requestPolicy.EnterAsync(
                TimeSpan.FromMilliseconds(Math.Clamp(_options.MinimumRequestSpacingMilliseconds, 250, 10000)), ct);

            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (psuContext is not null)
                foreach (var header in psuContext.HeadersFor(requiredPsuHeaders))
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (serializedBody is not null)
                request.Content = new StringContent(serializedBody, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(content))
                    return JsonDocument.Parse("{}").RootElement.Clone();
                using var doc = JsonDocument.Parse(content);
                return doc.RootElement.Clone();
            }

            var errorCode = TryGetErrorCode(content);
            var retryAt = GetRetryAt(response);

            // Never blind-retry an ASPSP/API rate limit. The sync service persists a bank-aware cooldown.
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
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
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
            ["kid"] = ApplicationId
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
        rsa.ImportFromPem(GetPrivateKeyPem());
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{B64(signature)}";
    }

    private string GetPrivateKeyPem()
    {
        if (_credentials is not null) return _credentials.PrivateKeyPem;
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyBase64))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(_options.PrivateKeyBase64.Trim()));
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Enable Banking legacy private key base64 is invalid.", ex);
            }
        }
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath) && File.Exists(_options.PrivateKeyPath))
            return File.ReadAllText(_options.PrivateKeyPath);
        throw new InvalidOperationException("Enable Banking legacy private key is not configured.");
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(ApplicationId))
            throw new InvalidOperationException("Enable Banking application ID is not configured.");
        if (_credentials is not null && string.IsNullOrWhiteSpace(_credentials.PrivateKeyPem))
            throw new InvalidOperationException("Enable Banking private key is not configured.");
        if (_credentials is null &&
            string.IsNullOrWhiteSpace(_options.PrivateKeyBase64) &&
            (string.IsNullOrWhiteSpace(_options.PrivateKeyPath) || !File.Exists(_options.PrivateKeyPath)))
            throw new InvalidOperationException("Enable Banking legacy private key is not configured.");
    }

    private static string B64(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
