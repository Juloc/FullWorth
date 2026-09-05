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

public interface IFullWorthCloudClient
{
    Uri BaseUri { get; }
    Task<FullWorthCloudRegistrationResult> RegisterAsync(Guid instanceId, string policyVersion, string clientVersion, CancellationToken ct);
    Task<FullWorthCloudRegistrationResult> RotateCredentialAsync(Guid instanceId, string currentCredential, CancellationToken ct);
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
