using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record FullWorthCloudRegistrationResult(
    Guid InstanceId,
    string Credential,
    DateTimeOffset? CredentialExpiresAt,
    string EntitlementStatus);

public sealed record FullWorthCloudBatchEventResult(string IdempotencyKey, string Status, string? ErrorCode);

public sealed record FullWorthCloudBatchResult(
    string BatchId,
    int Accepted,
    int Duplicate,
    int Rejected,
    IReadOnlyList<FullWorthCloudBatchEventResult> Events);

public sealed record FullWorthCloudSubmissionEvent(
    string IdempotencyKey,
    string SchemaVersion,
    string EventType,
    JsonElement Payload);

public sealed record FullWorthCloudBenchmark(
    string MetricKey,
    string? Currency,
    string? Country,
    string? RegionBucket,
    string? HouseholdSizeBand,
    string? IncomeBand,
    string? AgeBand,
    string? ObservedMonth,
    int ObservationCount,
    int DistinctInstanceCount,
    decimal Median,
    decimal Mean,
    decimal P25,
    decimal P75,
    decimal Min,
    decimal Max,
    string? EntityKey = null);

public sealed record FullWorthCloudPrice(
    string ProductKey,
    string? MerchantKey,
    string? Country,
    string Currency,
    string Bucket,
    int ObservationCount,
    int DistinctInstanceCount,
    decimal Median,
    decimal Mean,
    decimal P25,
    decimal P75,
    decimal Min,
    decimal Max);

public interface IFullWorthCloudClient
{
    Uri BaseUri { get; }
    /// <param name="currentCredential">
    /// The credential this instance currently holds, or null on a first enrollment. Registering an
    /// instance id the Cloud already knows revokes that instance's live credential, so the Cloud only
    /// allows it for a caller that proves it holds one of that instance's credentials - including an
    /// expired or already-revoked one, which is exactly what an instance renewing itself has.
    /// </param>
    Task<FullWorthCloudRegistrationResult> RegisterAsync(Guid instanceId, string policyVersion, string clientVersion, string? currentCredential, CancellationToken ct);
    Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(Guid instanceId, string currentCredential, CancellationToken ct);
    Task<FullWorthCloudBatchResult> SubmitBatchAsync(Guid instanceId, string instanceCredential, IReadOnlyList<FullWorthCloudSubmissionEvent> events, CancellationToken ct);
    Task<FullWorthCloudBenchmark?> GetBenchmarkAsync(
        string instanceCredential,
        string metricKey,
        string? currency,
        string? country,
        string? regionBucket,
        string? householdSizeBand,
        string? incomeBand,
        string? ageBand,
        string? observedMonth,
        CancellationToken ct);
    Task<FullWorthCloudBenchmark?> GetEntityBenchmarkAsync(
        string instanceCredential,
        string metricKey,
        string entityKey,
        string? currency,
        string? country,
        string? regionBucket,
        string? householdSizeBand,
        string? incomeBand,
        string? ageBand,
        string? observedMonth,
        CancellationToken ct) =>
        Task.FromException<FullWorthCloudBenchmark?>(
            new NotSupportedException("This cloud client does not support entity-specific benchmarks."));
    Task<FullWorthCloudPrice?> GetPriceAsync(
        string instanceCredential,
        string productKey,
        string currency,
        string? country,
        string? merchantKey,
        string bucket,
        CancellationToken ct) =>
        Task.FromResult<FullWorthCloudPrice?>(null);

    Task<KnowledgePackManifest?> GetLatestKnowledgePackManifestAsync(
        string instanceCredential,
        string? currentVersion,
        string? region,
        CancellationToken ct);
    Task<byte[]> DownloadKnowledgePackAsync(
        string instanceCredential,
        string packId,
        string version,
        CancellationToken ct);
    /// <summary>
    /// Fetches a transport-only delta from <paramref name="baseVersion"/> to <paramref name="version"/>, or
    /// null when the server has no delta (unknown/too-old base) and the client should do a full download.
    /// Optional: the default returns null so clients/test doubles that predate deltas simply skip the fast path.
    /// </summary>
    Task<KnowledgePackDelta?> DownloadKnowledgePackDeltaAsync(
        string instanceCredential,
        string packId,
        string version,
        string baseVersion,
        CancellationToken ct) =>
        Task.FromResult<KnowledgePackDelta?>(null);
    Task<byte[]> DownloadKnowledgePackBrandAssetAsync(
        string instanceCredential,
        string contentSha256,
        CancellationToken ct) =>
        throw new NotSupportedException("This cloud client does not provide brand-asset downloads.");
}

public sealed class FullWorthCloudException(
    string errorCode,
    HttpStatusCode? statusCode = null,
    TimeSpan? retryAfter = null,
    bool transient = false,
    string? message = null,
    Exception? innerException = null)
    : Exception(message ?? errorCode, innerException)
{
    public string ErrorCode { get; } = errorCode;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool Transient { get; } = transient;

    /// <summary>
    /// What the Cloud says the operator should DO about it. The Cloud sends one with every error;
    /// this client used to throw the response body away and derive a code from the HTTP status alone,
    /// so a precise "your instance is not entitled to this metric" arrived as \"cloud_forbidden\" with
    /// no advice, and the UI showed the bare token.
    /// </summary>
    public string? Remediation { get; init; }
}

/// <summary>
/// Typed client for the FullWorth Platform Cloud.
///
/// The endpoint is fixed outside Development, and the reason is not preference: the Cloud SERVER is a
/// private repository. FullWorthCloud:BaseUrl promised a self-hoster they could point the instance at
/// their own Cloud, and nobody outside can build one - so the setting could only ever send finance
/// observations to a host that is not a FullWorth Cloud.
///
/// What it must never do is silently redirect. Before this the value was read and then thrown away
/// outside Development, so an operator who entered their own Cloud kept sending to api.fullworth.de
/// with no error and no warning - their data went somewhere they had not chosen, which is the worst of
/// the three possible behaviours. So a configured endpoint that is not the official one now DISABLES
/// the Cloud client, with a reason the operator can read. The Cloud is optional (see the self-hosted
/// rules), so switching it off costs local finance features nothing.
///
/// Development and Testing still point anywhere - that is where a local cloud is the whole point, and
/// where this repository's own tests run.
/// </summary>
public sealed class FullWorthCloudClient : IFullWorthCloudClient
{
    public const string OfficialBaseUrl = "https://api.fullworth.de/";

    /// <summary>
    /// Reported when an instance configured a Cloud endpoint that is not the official one outside
    /// Development. The client stays constructible and every call fails with this - which is the point:
    /// the alternative was sending that operator's observations to a Cloud they did not choose.
    /// </summary>
    public const string EndpointNotConfigurableErrorCode = "cloud_endpoint_not_configurable";

    /// <summary>
    /// The Cloud's answer when this build is older than it serves. Nothing about it improves by
    /// waiting - only an update fixes it - so it is the one registration failure that must not be
    /// retried on the normal cadence.
    /// </summary>
    public const string ClientTooOldErrorCode = "client_too_old";
    public const int MaximumBatchEvents = 500;
    public const int MaximumCompressedBatchBytes = 2 * 1024 * 1024;
    public const int MaximumKnowledgePackBytes = 5 * 1024 * 1024;
    public const int MaximumBrandAssetBytes = 256 * 1024;

    private readonly HttpClient http;
    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;
    private readonly bool endpointRefused;

    public FullWorthCloudClient(HttpClient http, IConfiguration configuration, IHostEnvironment environment)
    {
        this.http = http;
        this.configuration = configuration;
        this.environment = environment;
        // Never throws: a misconfigured Cloud endpoint must not take down the instance with it. The
        // Cloud is optional, so the honest outcome is a client that refuses to talk and says why.
        endpointRefused = EndpointIsRefused(configuration, environment);
        http.BaseAddress = new Uri(OfficialBaseUrl);
        if (!endpointRefused)
        {
            try { http.BaseAddress = ResolveBaseUri(configuration, environment); }
            catch (InvalidOperationException) { endpointRefused = true; }
        }
        http.Timeout = TimeSpan.FromSeconds(45);
    }

    /// <summary>
    /// Whether this instance configured a Cloud endpoint it may not use. Checked once, in the
    /// constructor, because configuration does not change under a running process.
    /// </summary>
    internal static bool EndpointIsRefused(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) return false;

        var configured = configuration["FullWorthCloud:BaseUrl"]?.Trim();
        if (string.IsNullOrWhiteSpace(configured)) return false;

        return !string.Equals(
            configured.TrimEnd('/') + "/", OfficialBaseUrl, StringComparison.OrdinalIgnoreCase);
    }

    public Uri BaseUri => http.BaseAddress ?? new Uri(OfficialBaseUrl);

    public async Task<FullWorthCloudRegistrationResult> RegisterAsync(
        Guid instanceId,
        string policyVersion,
        string clientVersion,
        string? currentCredential,
        CancellationToken ct)
    {
        // External self-hosted instances register publicly against the official Cloud and must not need any
        // private Cloud-server secret. The shared enrollment token is therefore OPTIONAL: it is only sent when
        // an operator configured one (same-host/private Cloud deployments that gate registration).
        var enrollment = configuration["FullWorthCloud:EnrollmentToken"]?.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/instances/register");
        if (!string.IsNullOrWhiteSpace(enrollment))
            request.Headers.TryAddWithoutValidation("X-FullWorth-Enrollment-Token", enrollment);
        // Proof that a re-registration is this instance renewing itself rather than someone who merely
        // learned its id. Omitted on a first enrollment, where there is nothing to prove yet.
        if (!string.IsNullOrWhiteSpace(currentCredential))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", currentCredential.Trim());
        request.Content = JsonContent(new
        {
            instanceId,
            policyVersion,
            clientVersion,
            // Which wire protocol this build speaks. A Cloud that no longer serves it refuses here,
            // once, instead of accepting the registration and then failing every later call.
            protocolVersion = CloudIntelligencePolicy.WireProtocolVersion
        });
        using var response = await SendAsync(request, ct);
        var result = await DeserializeAsync<RegistrationResponse>(response, ct);
        if (result.InstanceId != instanceId || string.IsNullOrWhiteSpace(result.Credential))
            throw new FullWorthCloudException("cloud_registration_invalid_response", response.StatusCode);
        return new(instanceId, result.Credential.Trim(), result.CredentialExpiresAt,
            NormalizeEntitlement(result.EntitlementStatus));
    }

    public async Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(
        Guid instanceId,
        string currentCredential,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/instances/rotate-credential");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentCredential);
        request.Content = JsonContent(new { instanceId });
        using var response = await SendAsync(request, ct);
        var result = await DeserializeAsync<RegistrationResponse>(response, ct);
        if (result.InstanceId != instanceId || string.IsNullOrWhiteSpace(result.Credential))
            throw new FullWorthCloudException("cloud_rotation_invalid_response", response.StatusCode);
        return new(instanceId, result.Credential.Trim(), result.CredentialExpiresAt,
            NormalizeEntitlement(result.EntitlementStatus));
    }

    public async Task<FullWorthCloudBatchResult> SubmitBatchAsync(
        Guid instanceId,
        string instanceCredential,
        IReadOnlyList<FullWorthCloudSubmissionEvent> events,
        CancellationToken ct)
    {
        if (events.Count is < 1 or > MaximumBatchEvents)
            throw new ArgumentOutOfRangeException(nameof(events));

        var batchId = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            batchId,
            instanceId,
            schemaVersion = CloudIntelligencePolicy.SubmissionSchemaVersion,
            events
        });
        var compressed = Compress(json);
        if (compressed.Length > MaximumCompressedBatchBytes)
            throw new FullWorthCloudException("cloud_batch_too_large");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/submissions/batch");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", $"batch:{instanceId:N}:{batchId}");
        request.Content = new ByteArrayContent(compressed);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentEncoding.Add("gzip");

        using var response = await SendAsync(request, ct);
        var result = await DeserializeAsync<BatchResponse>(response, ct);
        var perEvent = result.Events?.Select(x => new FullWorthCloudBatchEventResult(
                x.IdempotencyKey ?? string.Empty,
                x.Status ?? string.Empty,
                x.ErrorCode))
            .Where(x => x.IdempotencyKey.Length > 0)
            .ToList() ?? [];
        return new FullWorthCloudBatchResult(
            result.BatchId ?? batchId,
            result.Accepted,
            result.Duplicate,
            result.Rejected,
            perEvent);
    }

    public async Task<FullWorthCloudBenchmark?> GetBenchmarkAsync(
        string instanceCredential,
        string metricKey,
        string? currency,
        string? country,
        string? regionBucket,
        string? householdSizeBand,
        string? incomeBand,
        string? ageBand,
        string? observedMonth,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(metricKey))
            throw new ArgumentException("Benchmark metric key is required.", nameof(metricKey));

        var query = new List<string> { $"metricKey={Uri.EscapeDataString(metricKey.Trim())}" };
        AddQuery(query, "currency", currency);
        AddQuery(query, "country", country);
        AddQuery(query, "regionBucket", regionBucket);
        AddQuery(query, "householdSizeBand", householdSizeBand);
        AddQuery(query, "incomeBand", incomeBand);
        AddQuery(query, "ageBand", ageBand);
        AddQuery(query, "observedMonth", observedMonth);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/benchmarks?{string.Join('&', query)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await DeserializeAsync<FullWorthCloudBenchmark>(response, ct);
    }

    public async Task<FullWorthCloudBenchmark?> GetEntityBenchmarkAsync(
        string instanceCredential,
        string metricKey,
        string entityKey,
        string? currency,
        string? country,
        string? regionBucket,
        string? householdSizeBand,
        string? incomeBand,
        string? ageBand,
        string? observedMonth,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(metricKey))
            throw new ArgumentException("Benchmark metric key is required.", nameof(metricKey));
        if (string.IsNullOrWhiteSpace(entityKey))
            throw new ArgumentException("Benchmark entity key is required.", nameof(entityKey));

        var query = new List<string>
        {
            $"metricKey={Uri.EscapeDataString(metricKey.Trim())}",
            $"entityKey={Uri.EscapeDataString(entityKey.Trim().ToLowerInvariant())}"
        };
        AddQuery(query, "currency", currency);
        AddQuery(query, "country", country);
        AddQuery(query, "regionBucket", regionBucket);
        AddQuery(query, "householdSizeBand", householdSizeBand);
        AddQuery(query, "incomeBand", incomeBand);
        AddQuery(query, "ageBand", ageBand);
        AddQuery(query, "observedMonth", observedMonth);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/benchmarks?{string.Join('&', query)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await DeserializeAsync<FullWorthCloudBenchmark>(response, ct);
    }

    public async Task<FullWorthCloudPrice?> GetPriceAsync(
        string instanceCredential,
        string productKey,
        string currency,
        string? country,
        string? merchantKey,
        string bucket,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productKey))
            throw new ArgumentException("Product key is required.", nameof(productKey));
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("Price bucket is required.", nameof(bucket));

        var query = new List<string>
        {
            $"productKey={Uri.EscapeDataString(productKey.Trim())}",
            $"currency={Uri.EscapeDataString(currency.Trim().ToUpperInvariant())}",
            $"bucket={Uri.EscapeDataString(bucket.Trim())}"
        };
        AddQuery(query, "country", country);
        AddQuery(query, "merchantKey", merchantKey);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/prices?{string.Join('&', query)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await DeserializeAsync<FullWorthCloudPrice>(response, ct);
    }

    public async Task<KnowledgePackManifest?> GetLatestKnowledgePackManifestAsync(
        string instanceCredential,
        string? currentVersion,
        string? region,
        CancellationToken ct)
    {
        var query = new List<string>();
        AddQuery(query, "currentVersion", currentVersion);
        AddQuery(query, "region", region);
        var suffix = query.Count == 0 ? string.Empty : "?" + string.Join('&', query);

        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/knowledge-packs/latest" + suffix);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await DeserializeAsync<KnowledgePackManifest>(response, ct);
    }

    public async Task<byte[]> DownloadKnowledgePackAsync(
        string instanceCredential,
        string packId,
        string version,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Knowledge-pack id and version are required.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/knowledge-packs/{Uri.EscapeDataString(packId.Trim())}/{Uri.EscapeDataString(version.Trim())}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct);
        if (response.Content.Headers.ContentLength is > MaximumKnowledgePackBytes)
            throw new FullWorthCloudException("knowledge_pack_size_invalid", response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            if (output.Length + read > MaximumKnowledgePackBytes)
                throw new FullWorthCloudException("knowledge_pack_size_invalid", response.StatusCode);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public async Task<KnowledgePackDelta?> DownloadKnowledgePackDeltaAsync(
        string instanceCredential,
        string packId,
        string version,
        string baseVersion,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packId) ||
            string.IsNullOrWhiteSpace(version) ||
            string.IsNullOrWhiteSpace(baseVersion))
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/knowledge-packs/{Uri.EscapeDataString(packId.Trim())}/{Uri.EscapeDataString(version.Trim())}/delta" +
            $"?base={Uri.EscapeDataString(baseVersion.Trim())}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct);
        // 204 = no delta available; the caller falls back to a full download.
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        if (response.Content.Headers.ContentLength is > MaximumKnowledgePackBytes)
            throw new FullWorthCloudException("knowledge_pack_size_invalid", response.StatusCode);
        return await DeserializeAsync<KnowledgePackDelta>(response, ct);
    }

    public async Task<byte[]> DownloadKnowledgePackBrandAssetAsync(
        string instanceCredential,
        string contentSha256,
        CancellationToken ct)
    {
        var hash = contentSha256?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("Brand asset SHA-256 is invalid.", nameof(contentSha256));

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/knowledge-packs/assets/{Uri.EscapeDataString(hash)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceCredential);
        using var response = await SendAsync(request, ct);
        if (response.Content.Headers.ContentLength is > MaximumBrandAssetBytes)
            throw new FullWorthCloudException("knowledge_pack_brand_asset_size_invalid", response.StatusCode);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            if (output.Length + read > MaximumBrandAssetBytes)
                throw new FullWorthCloudException("knowledge_pack_brand_asset_size_invalid", response.StatusCode);
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    internal static Uri ResolveBaseUri(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["FullWorthCloud:BaseUrl"]?.Trim();
        var isDevelopment = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var value = string.IsNullOrWhiteSpace(configured) ? OfficialBaseUrl : configured;

        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("FullWorth Cloud base URL is invalid.");

        if (isDevelopment) return uri;

        // Anonymised observations still describe someone's finances, so the transport is not optional.
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                "FullWorth Cloud base URL must use HTTPS. Set FullWorthCloud:BaseUrl to an https:// endpoint.");

        // A Cloud reached over the public internet cannot live on loopback or inside a private range;
        // a value like that is a copied development setting, and failing loudly beats silently sending
        // to a host that answers nothing.
        if (uri.IsLoopback || IsPrivateHost(uri))
            throw new InvalidOperationException(
                "FullWorth Cloud base URL must be a public host outside Development.");

        return uri;
    }

    private static bool IsPrivateHost(Uri uri)
    {
        if (!System.Net.IPAddress.TryParse(uri.Host, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,
            127 => true,
            172 => octets[1] >= 16 && octets[1] <= 31,
            192 => octets[1] == 168,
            169 => octets[1] == 254,
            _ => false
        };
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // One guard for every call, because every call goes through here. Not transient: nothing about
        // a configured endpoint changes by waiting, and a transient error is what the retry loops in
        // this module are built to keep trying.
        if (endpointRefused)
            throw new FullWorthCloudException(EndpointNotConfigurableErrorCode)
            {
                Remediation =
                    "This build talks to the official FullWorth Cloud only. Remove FullWorthCloud:BaseUrl " +
                    "to use it, or leave Cloud off - local finance features do not need it."
            };

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FullWorthCloudException("cloud_timeout", transient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new FullWorthCloudException("cloud_unreachable", transient: true, innerException: ex);
        }

        if (response.IsSuccessStatusCode) return response;

        var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
        var status = response.StatusCode;
        var statusCode = status switch
        {
            HttpStatusCode.Unauthorized => "cloud_unauthorized",
            HttpStatusCode.Forbidden => "cloud_entitlement_denied",
            HttpStatusCode.TooManyRequests => "cloud_rate_limited",
            HttpStatusCode.RequestEntityTooLarge => "cloud_batch_too_large",
            _ when (int)status >= 500 => "cloud_server_error",
            _ => $"cloud_http_{(int)status}"
        };
        var transient = status == HttpStatusCode.TooManyRequests || (int)status >= 500;

        // The Cloud states the real reason and what to do about it in the body. Reading only the
        // status code threw that away, so every 403 looked the same and the UI could only show a bare
        // token. The status-derived code stays as the fallback for a body we cannot read - including a
        // reverse proxy answering instead of the Cloud.
        var reported = await ReadErrorAsync(response, ct);
        response.Dispose();
        throw new FullWorthCloudException(
            string.IsNullOrWhiteSpace(reported.ErrorCode) ? statusCode : reported.ErrorCode!,
            status,
            retryAfter,
            transient,
            reported.Message)
        {
            Remediation = reported.Remediation
        };
    }

    /// <summary>
    /// Reads the Cloud's error contract. Never throws: an unreadable body only means the caller falls
    /// back to the status-derived code, and a failure to parse an error must not replace the error.
    /// </summary>
    private static async Task<(string? ErrorCode, string? Message, string? Remediation)> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            if (response.Content.Headers.ContentType?.MediaType is not "application/json"
                and not "application/problem+json")
                return (null, null, null);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body) || body.Length > 8 * 1024) return (null, null, null);
            var reported = JsonSerializer.Deserialize<CloudErrorBody>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            // A machine code has to look like one; a sentence in the errorCode field is not a code.
            var code = reported?.ErrorCode?.Trim();
            if (code is { Length: > 64 } or "") code = null;
            return (code, Trim(reported?.Message), Trim(reported?.Remediation));
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or InvalidOperationException)
        {
            return (null, null, null);
        }

        static string? Trim(string? value)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 500)];
        }
    }

    private sealed record CloudErrorBody(string? ErrorCode, string? Message, string? Remediation);

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                   ?? throw new JsonException("Empty response.");
        }
        catch (JsonException ex)
        {
            throw new FullWorthCloudException("cloud_invalid_json", response.StatusCode, message: "Cloud response was invalid.", innerException: ex);
        }
    }

    private static ByteArrayContent JsonContent<T>(T value)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(input, 0, input.Length);
        return output.ToArray();
    }

    private static void AddQuery(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            query.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
    }

    private static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private static string NormalizeEntitlement(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();
        return normalized.Length <= 80 ? normalized : normalized[..80];
    }

    private sealed record RegistrationResponse(
        Guid InstanceId,
        string Credential,
        DateTimeOffset? CredentialExpiresAt,
        string? EntitlementStatus);

    private sealed record BatchResponse(
        string? BatchId,
        int Accepted,
        int Duplicate,
        int Rejected,
        List<BatchEventResponse>? Events);

    private sealed record BatchEventResponse(string? IdempotencyKey, string? Status, string? ErrorCode);
}

public sealed class CloudInstanceCredentialStore(
    IntelligenceDbContext db,
    FieldCipher cipher)
{
    public async Task<CloudInstanceCredential?> GetAsync(Guid instanceId, CancellationToken ct) =>
        await db.CloudInstanceCredentials.SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct);

    public async Task<string?> GetSecretAsync(Guid instanceId, CancellationToken ct)
    {
        var row = await db.CloudInstanceCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct);
        return row is null ? null : cipher.Unprotect(row.ProtectedSecret);
    }

    public async Task<CloudInstanceCredential> SaveAsync(FullWorthCloudRegistrationResult registration, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.CloudInstanceCredentials.SingleOrDefaultAsync(x => x.InstanceId == registration.InstanceId, ct);
        if (row is null)
        {
            row = new CloudInstanceCredential { InstanceId = registration.InstanceId, IssuedAt = now };
            db.CloudInstanceCredentials.Add(row);
        }

        row.ProtectedSecret = cipher.Protect(registration.Credential)
            ?? throw new InvalidOperationException("Cloud instance credential encryption failed.");
        row.SecretFingerprint = Fingerprint(registration.Credential);
        row.IssuedAt = now;
        row.ExpiresAt = registration.CredentialExpiresAt;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task MarkUsedAsync(Guid instanceId, CancellationToken ct)
    {
        var row = await db.CloudInstanceCredentials.SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct);
        if (row is null) return;
        row.LastUsedAt = DateTimeOffset.UtcNow;
        row.UpdatedAt = row.LastUsedAt.Value;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid instanceId, CancellationToken ct)
    {
        var row = await db.CloudInstanceCredentials.SingleOrDefaultAsync(x => x.InstanceId == instanceId, ct);
        if (row is null) return;
        db.CloudInstanceCredentials.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private static string Fingerprint(string secret)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
        return $"sha256:{hash[..16]}";
    }
}
