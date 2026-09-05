using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.EnableBanking;

public sealed record EnableBankingAutoRegistrationRequest(string Email, string Environment);

public sealed record EnableBankingAutoRegistrationStart(
    string Id,
    string Status,
    string SetupCallbackUrl,
    string PrivacyUrl,
    string TermsUrl);

public sealed record EnableBankingAutoRegistrationView(
    string Id,
    string Status,
    string? ErrorCode,
    string? ApplicationId,
    bool CanRetryVerification);

public sealed record EnableBankingAutoRegistrationCallbackResult(
    bool Success,
    string Status,
    string? ErrorCode);

/// <summary>
/// One-time Enable Banking Control Panel sign-in + application registration flow.
/// The flow mirrors Enable Banking's official CLI: request an email-link sign-in, exchange the
/// oobCode for a short-lived Control Panel token, POST /api/applications, then immediately discard
/// the Control Panel tokens. Generated RSA private keys remain server-side and are persisted only
/// after the newly registered application has been verified through GET /application.
/// </summary>
public sealed class EnableBankingControlPanelRegistrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, PendingRegistration> _pending = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EnableBankingOptions _options;
    private readonly ILogger<EnableBankingControlPanelRegistrationService> _logger;

    public EnableBankingControlPanelRegistrationService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<EnableBankingOptions> options,
        ILogger<EnableBankingControlPanelRegistrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EnableBankingAutoRegistrationStart> StartAsync(
        Guid userId,
        EnableBankingAutoRegistrationRequest request,
        CancellationToken ct)
    {
        PruneExpired();

        if (userId == Guid.Empty)
            throw new ArgumentException("A FullWorth user is required.");

        var email = (request.Email ?? string.Empty).Trim();
        if (email.Length is < 3 or > 254 || !MailAddress.TryCreate(email, out _))
            throw new ArgumentException("Enter a valid email address.");

        var environment = (request.Environment ?? string.Empty).Trim().ToUpperInvariant();
        if (environment is not ("SANDBOX" or "PRODUCTION"))
            throw new ArgumentException("Environment must be SANDBOX or PRODUCTION.");

        if (string.IsNullOrWhiteSpace(_options.RedirectUrl) ||
            !Uri.TryCreate(_options.RedirectUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("EnableBanking:RedirectUrl is not configured with an absolute URL.");

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<EnableBankingProfileService>();
            var status = await profiles.GetStatusAsync(userId, ct);
            if (status.Configured)
                throw new InvalidOperationException("An Enable Banking profile is already configured.");
        }

        var id = RandomToken(32);
        var setupCallbackUrl = BuildSetupCallbackUrl(id);

        using var rsa = RSA.Create(4096);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var pending = new PendingRegistration
        {
            Id = id,
            UserId = userId,
            Email = email,
            Environment = environment,
            PrivateKeyPem = privateKeyPem,
            PublicKeyPem = publicKeyPem,
            SetupCallbackUrl = setupCallbackUrl,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20),
            Status = "waiting_for_email"
        };

        if (!_pending.TryAdd(id, pending))
            throw new InvalidOperationException("Unable to create Enable Banking setup state.");

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                requestType = "EMAIL_SIGNIN",
                email,
                continueUrl = setupCallbackUrl,
                canHandleCodeInApp = true
            }, JsonOptions);

            var response = await SendControlPanelAsync(
                HttpMethod.Post,
                "/api/relyingparty/getOobConfirmationCode",
                new StringContent(body, Encoding.UTF8, "application/json"),
                bearerToken: null,
                ct);

            if (!IsSuccess(response.StatusCode))
                throw new EnableBankingControlPanelException(
                    "control_panel_login_start_failed",
                    response.StatusCode);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            CryptographicOperations.ZeroMemory(Encoding.UTF8.GetBytes(privateKeyPem));
            throw;
        }

        return new(
            id,
            pending.Status,
            setupCallbackUrl,
            _options.PrivacyUrl,
            _options.TermsUrl);
    }

    public bool Cancel(Guid userId, string id)
    {
        if (!_pending.TryGetValue(id, out var pending) || pending.UserId != userId)
            return false;

        if (Interlocked.CompareExchange(ref pending.Claimed, 1, 0) != 0)
            return false;

        try
        {
            if (!_pending.TryRemove(id, out var removed))
                return false;

            removed.PrivateKeyPem = string.Empty;
            removed.PublicKeyPem = string.Empty;
            removed.Email = string.Empty;
            removed.Status = "cancelled";
            return true;
        }
        finally
        {
            Volatile.Write(ref pending.Claimed, 0);
        }
    }

    public EnableBankingAutoRegistrationView GetStatus(Guid userId, string id)
    {
        PruneExpired();

        if (!_pending.TryGetValue(id, out var pending) || pending.UserId != userId)
            throw new KeyNotFoundException();

        if (pending.ExpiresAt <= DateTimeOffset.UtcNow && pending.Status is not ("completed" or "failed"))
            pending.Status = "expired";

        return View(pending);
    }

    public async Task<EnableBankingAutoRegistrationView> RetryVerificationAsync(
        Guid userId,
        string id,
        CancellationToken ct)
    {
        PruneExpired();

        if (!_pending.TryGetValue(id, out var pending) || pending.UserId != userId)
            throw new KeyNotFoundException();

        if (string.IsNullOrWhiteSpace(pending.ApplicationId) || string.IsNullOrWhiteSpace(pending.PrivateKeyPem))
            throw new InvalidOperationException("No registered application is available for verification retry.");

        if (Interlocked.CompareExchange(ref pending.Claimed, 1, 0) != 0)
            return View(pending);

        try
        {
            pending.Status = "verifying";
            pending.ErrorCode = null;
            await VerifyAndPersistAsync(pending, ct);
            pending.Status = "completed";
            pending.PrivateKeyPem = string.Empty;
            pending.PublicKeyPem = string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pending.Status = "failed";
            pending.ErrorCode = "application_verification_failed";
            _logger.LogWarning(ex, "Enable Banking application verification retry failed.");
        }
        finally
        {
            Volatile.Write(ref pending.Claimed, 0);
        }

        return View(pending);
    }

    public async Task<EnableBankingAutoRegistrationCallbackResult> CompleteAsync(
        string? id,
        string? oobCode,
        CancellationToken ct)
    {
        PruneExpired();

        if (string.IsNullOrWhiteSpace(id) ||
            !_pending.TryGetValue(id, out var pending) ||
            pending.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(false, "expired", "setup_expired");

        if (string.IsNullOrWhiteSpace(oobCode))
            return new(false, pending.Status, "missing_oob_code");

        if (Interlocked.CompareExchange(ref pending.Claimed, 1, 0) != 0)
            return new(pending.Status == "completed", pending.Status, pending.ErrorCode);

        try
        {
            pending.Status = "registering";
            pending.ErrorCode = null;

            var signInBody = JsonSerializer.Serialize(new
            {
                oobCode,
                email = pending.Email
            }, JsonOptions);

            var signIn = await SendControlPanelAsync(
                HttpMethod.Post,
                "/api/relyingparty/emailLinkSignin",
                new StringContent(signInBody, Encoding.UTF8, "application/json"),
                bearerToken: null,
                ct);

            if (!IsSuccess(signIn.StatusCode))
                throw new EnableBankingControlPanelException(
                    "control_panel_login_complete_failed",
                    signIn.StatusCode);

            using var signInJson = JsonDocument.Parse(signIn.Body);
            var idToken = GetString(signInJson.RootElement, "idToken");
            var refreshToken = GetString(signInJson.RootElement, "refreshToken");
            if (string.IsNullOrWhiteSpace(idToken))
                throw new EnableBankingControlPanelException("control_panel_token_missing", signIn.StatusCode);

            var registration = await RegisterApplicationAsync(pending, idToken, ct);
            if (registration.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(refreshToken))
            {
                var refreshed = await RefreshControlPanelTokenAsync(refreshToken, ct);
                if (!string.IsNullOrWhiteSpace(refreshed))
                    registration = await RegisterApplicationAsync(pending, refreshed, ct);
            }

            if (!IsSuccess(registration.StatusCode))
                throw new EnableBankingControlPanelException(
                    "application_registration_failed",
                    registration.StatusCode);

            using var registrationJson = JsonDocument.Parse(registration.Body);
            var applicationId = GetString(registrationJson.RootElement, "app_id");
            if (string.IsNullOrWhiteSpace(applicationId))
                throw new EnableBankingControlPanelException(
                    "application_id_missing",
                    registration.StatusCode);

            pending.ApplicationId = applicationId;
            pending.Status = "verifying";

            await VerifyAndPersistAsync(pending, ct);

            pending.Status = "completed";
            pending.ErrorCode = null;
            pending.PrivateKeyPem = string.Empty;
            pending.PublicKeyPem = string.Empty;
            pending.Email = string.Empty;

            return new(true, pending.Status, null);
        }
        catch (EnableBankingControlPanelException ex)
        {
            pending.Status = "failed";
            pending.ErrorCode = ex.SafeCode;
            if (string.IsNullOrWhiteSpace(pending.ApplicationId))
            {
                pending.PrivateKeyPem = string.Empty;
                pending.PublicKeyPem = string.Empty;
                pending.Email = string.Empty;
            }
            _logger.LogWarning(
                "Enable Banking Control Panel registration failed with {StatusCode} ({SafeCode}).",
                (int)ex.StatusCode,
                ex.SafeCode);
            return new(false, pending.Status, pending.ErrorCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pending.Status = "failed";
            pending.ErrorCode = string.IsNullOrWhiteSpace(pending.ApplicationId)
                ? "application_registration_failed"
                : "application_verification_failed";
            if (string.IsNullOrWhiteSpace(pending.ApplicationId))
            {
                pending.PrivateKeyPem = string.Empty;
                pending.PublicKeyPem = string.Empty;
                pending.Email = string.Empty;
            }
            _logger.LogWarning(ex, "Enable Banking automatic application setup failed.");
            return new(false, pending.Status, pending.ErrorCode);
        }
        finally
        {
            Volatile.Write(ref pending.Claimed, 0);
        }
    }

    private async Task VerifyAndPersistAsync(PendingRegistration pending, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var profiles = scope.ServiceProvider.GetRequiredService<EnableBankingProfileService>();
        await profiles.VerifyAndSaveAsync(
            pending.UserId,
            new EnableBankingProfileVerifyRequest(
                pending.ApplicationId!,
                pending.PrivateKeyPem),
            ct);
    }

    private async Task<ControlPanelResponse> RegisterApplicationAsync(
        PendingRegistration pending,
        string idToken,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>
        {
            ["certificate"] = pending.PublicKeyPem,
            ["environment"] = pending.Environment,
            ["name"] = _options.ApplicationName,
            ["redirect_urls"] = new[] { _options.RedirectUrl }
        };

        if (pending.Environment == "PRODUCTION")
        {
            data["description"] = _options.ApplicationDescription;
            data["gdpr_email"] = pending.Email;
            data["privacy_url"] = _options.PrivacyUrl;
            data["terms_url"] = _options.TermsUrl;
        }

        var body = JsonSerializer.Serialize(data, JsonOptions);
        return await SendControlPanelAsync(
            HttpMethod.Post,
            "/api/applications",
            new StringContent(body, Encoding.UTF8, "application/json"),
            idToken,
            ct);
    }

    private async Task<string?> RefreshControlPanelTokenAsync(
        string refreshToken,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        });

        var response = await SendControlPanelAsync(
            HttpMethod.Post,
            "/api/token",
            content,
            bearerToken: null,
            ct);

        if (!IsSuccess(response.StatusCode))
            return null;

        using var json = JsonDocument.Parse(response.Body);
        return GetString(json.RootElement, "id_token");
    }

    private async Task<ControlPanelResponse> SendControlPanelAsync(
        HttpMethod method,
        string path,
        HttpContent content,
        string? bearerToken,
        CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("enable-banking-control-panel");
        using var request = new HttpRequestMessage(method, path);
        request.Content = content;

        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return new(response.StatusCode, body);
    }

    private string BuildSetupCallbackUrl(string id)
    {
        var redirect = new Uri(_options.RedirectUrl, UriKind.Absolute);
        var builder = new UriBuilder(redirect)
        {
            Path = "/connect/enable-banking/setup-callback",
            Query = $"state={Uri.EscapeDataString(id)}",
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        foreach (var pair in _pending)
        {
            var pending = pair.Value;
            if (pending.ExpiresAt < cutoff || (pending.Status == "completed" && pending.ExpiresAt < DateTimeOffset.UtcNow))
                _pending.TryRemove(pair.Key, out _);
        }
    }

    private static EnableBankingAutoRegistrationView View(PendingRegistration pending) => new(
        pending.Id,
        pending.Status,
        pending.ErrorCode,
        pending.ApplicationId,
        pending.Status == "failed" &&
        !string.IsNullOrWhiteSpace(pending.ApplicationId) &&
        !string.IsNullOrWhiteSpace(pending.PrivateKeyPem));

    private static string? GetString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsSuccess(HttpStatusCode code) => (int)code is >= 200 and <= 299;

    private static string RandomToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record ControlPanelResponse(HttpStatusCode StatusCode, string Body);

    private sealed class PendingRegistration
    {
        public required string Id { get; init; }
        public Guid UserId { get; init; }
        public string Email { get; set; } = string.Empty;
        public string Environment { get; init; } = "PRODUCTION";
        public string PrivateKeyPem { get; set; } = string.Empty;
        public string PublicKeyPem { get; set; } = string.Empty;
        public string SetupCallbackUrl { get; init; } = string.Empty;
        public string? ApplicationId { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public string Status { get; set; } = "waiting_for_email";
        public string? ErrorCode { get; set; }
        public int Claimed;
    }
}

public sealed class EnableBankingControlPanelException : Exception
{
    public EnableBankingControlPanelException(string safeCode, HttpStatusCode statusCode)
        : base(safeCode)
    {
        SafeCode = safeCode;
        StatusCode = statusCode;
    }

    public string SafeCode { get; }
    public HttpStatusCode StatusCode { get; }
}
