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
    string? EntityKey,
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
    decimal Max);

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
    Task<FullWorthCloudRegistrationResult> RegisterAsync(Guid instanceId, string policyVersion, string clientVersion, CancellationToken ct);
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
}

/// <summary>
/// Typed client for the official FullWorth Platform Cloud. Production always targets the compiled
/// official endpoint; only Development/Testing builds may override it for local cloud development.
/// </summary>
public sealed class FullWorthCloudClient : IFullWorthCloudClient
{
    public const string OfficialBaseUrl = "https://api.fullworth.de/";
    public const int MaximumBatchEvents = 500;
    public const int MaximumCompressedBatchBytes = 2 * 1024 * 1024;
    public const int MaximumKnowledgePackBytes = 5 * 1024 * 1024;
    public const int MaximumBrandAssetBytes = 256 * 1024;

    private readonly HttpClient http;
    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;

    public FullWorthCloudClient(HttpClient http, IConfiguration configuration, IHostEnvironment environment)
    {
        this.http = http;
        this.configuration = configuration;
        this.environment = environment;
        http.BaseAddress = ResolveBaseUri(configuration, environment);
        http.Timeout = TimeSpan.FromSeconds(45);
    }

    public Uri BaseUri => http.BaseAddress ?? new Uri(OfficialBaseUrl);

    public async Task<FullWorthCloudRegistrationResult> RegisterAsync(
        Guid instanceId,
        string policyVersion,
        string clientVersion,
        CancellationToken ct)
    {
        var enrollment = configuration["FullWorthCloud:EnrollmentToken"]?.Trim();
        if (string.IsNullOrWhiteSpace(enrollment))
            throw new FullWorthCloudException("cloud_enrollment_missing");

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/instances/register");
        request.Headers.TryAddWithoutValidation("X-FullWorth-Enrollment-Token", enrollment);
        request.Content = JsonContent(new
        {
            instanceId,
            policyVersion,
            clientVersion
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
        var allowOverride = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var value = allowOverride && !string.IsNullOrWhiteSpace(configured) ? configured : OfficialBaseUrl;
        if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            throw new InvalidOperationException("FullWorth Cloud base URL is invalid.");
        if (!allowOverride && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("FullWorth Cloud production endpoint must use HTTPS.");
        return uri;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
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
        var errorCode = status switch
        {
            HttpStatusCode.Unauthorized => "cloud_unauthorized",
            HttpStatusCode.Forbidden => "cloud_entitlement_denied",
            HttpStatusCode.TooManyRequests => "cloud_rate_limited",
            HttpStatusCode.RequestEntityTooLarge => "cloud_batch_too_large",
            _ when (int)status >= 500 => "cloud_server_error",
            _ => $"cloud_http_{(int)status}"
        };
        var transient = status == HttpStatusCode.TooManyRequests || (int)status >= 500;
        response.Dispose();
        throw new FullWorthCloudException(errorCode, status, retryAfter, transient);
    }

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
