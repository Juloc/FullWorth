using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Banking.Backend;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.EnableBanking;

public sealed record EnableBankingProfileVerifyRequest(string ApplicationId, string PrivateKeyPem);

public sealed record EnableBankingProfileView(
    Guid Id,
    string ApplicationId,
    string KeyFingerprint,
    string Environment,
    string ApplicationName,
    bool Active,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RedirectUrls,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset UpdatedAt);

public sealed record EnableBankingSetupStatus(
    bool Configured,
    bool LegacyConfigured,
    string CallbackUrl,
    EnableBankingProfileView? Profile);

public sealed class EnableBankingProfileService(
    EnableBankingClientResolver resolver,
    FullWorthBackendClient backend,
    IOptions<EnableBankingOptions> options)
{
    private readonly EnableBankingOptions _options = options.Value;

    public async Task<EnableBankingSetupStatus> GetStatusAsync(Guid userId, CancellationToken ct)
    {
        var profile = await resolver.GetProfileForUserAsync(userId, ct);
        return new(
            profile is not null,
            resolver.LegacyConfigured,
            _options.RedirectUrl,
            profile is null ? null : View(profile));
    }

    public async Task<EnableBankingProfileView> VerifyAndSaveAsync(
        Guid userId,
        EnableBankingProfileVerifyRequest request,
        CancellationToken ct)
    {
        var applicationId = (request.ApplicationId ?? string.Empty).Trim();
        var privateKeyPem = request.PrivateKeyPem ?? string.Empty;
        if (applicationId.Length is < 8 or > 128)
            throw new ArgumentException("Invalid Enable Banking application ID.");
        if (privateKeyPem.Length is < 100 or > 32768)
            throw new ArgumentException("Invalid Enable Banking private key.");

        var fingerprint = PublicKeyFingerprint(privateKeyPem);
        var client = resolver.CreateTemporary(applicationId, privateKeyPem);
        var application = await client.GetApplicationAsync(ct);
        var verified = ValidateApplication(applicationId, application);

        var stored = await backend.UpsertEnableBankingProfileAsync(new(
            userId,
            applicationId,
            privateKeyPem,
            fingerprint,
            verified.Environment,
            verified.ApplicationName,
            verified.Active,
            verified.Services,
            verified.RedirectUrls,
            DateTimeOffset.UtcNow), ct);

        return View(stored);
    }

    public async Task<EnableBankingProfileView> RecheckAsync(Guid userId, CancellationToken ct)
    {
        var profile = await backend.GetEnableBankingProfileForUserAsync(userId, ct)
            ?? throw new EnableBankingProfileNotConfiguredException("No Enable Banking profile is configured.");

        var client = resolver.CreateTemporary(profile.ApplicationId, profile.PrivateKeyPem);
        var application = await client.GetApplicationAsync(ct);
        var verified = ValidateApplication(profile.ApplicationId, application);

        var stored = await backend.UpsertEnableBankingProfileAsync(new(
            userId,
            profile.ApplicationId,
            profile.PrivateKeyPem,
            profile.KeyFingerprint,
            verified.Environment,
            verified.ApplicationName,
            verified.Active,
            verified.Services,
            verified.RedirectUrls,
            DateTimeOffset.UtcNow), ct);

        return View(stored);
    }

    public Task<System.Net.HttpStatusCode> DeleteAsync(Guid userId, CancellationToken ct) =>
        backend.DeleteEnableBankingProfileForUserAsync(userId, ct);

    private VerifiedApplication ValidateApplication(string expectedApplicationId, JsonElement application)
    {
        if (application.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Enable Banking /application returned an invalid response.");

        var kid = GetString(application, "kid");
        if (!string.Equals(kid, expectedApplicationId, StringComparison.Ordinal))
            throw new InvalidOperationException("Enable Banking application ID does not match the supplied private key.");

        var environment = (GetString(application, "environment") ?? string.Empty).ToUpperInvariant();
        if (environment is not ("SANDBOX" or "PRODUCTION"))
            throw new InvalidOperationException("Unsupported Enable Banking application environment.");

        var services = GetStrings(application, "services");
        if (!services.Contains("AIS", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enable Banking application does not have AIS enabled.");

        var redirectUrls = GetStrings(application, "redirect_urls");
        if (string.IsNullOrWhiteSpace(_options.RedirectUrl))
            throw new InvalidOperationException("EnableBanking:RedirectUrl is not configured on this FullWorth instance.");
        if (!redirectUrls.Contains(_options.RedirectUrl, StringComparer.Ordinal))
            throw new InvalidOperationException("FullWorth callback URL is not registered in the Enable Banking application.");

        var active = application.TryGetProperty("active", out var activeElement) &&
                     activeElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                     activeElement.GetBoolean();

        return new(
            GetString(application, "name") ?? "Enable Banking",
            environment,
            active,
            services,
            redirectUrls);
    }

    private static string PublicKeyFingerprint(string privateKeyPem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            // ImportFromPem also accepts PUBLIC KEY material. Require private parameters here so an
            // invalid upload becomes a deterministic 400 before any JWT signing/provider request.
            _ = rsa.ExportParameters(includePrivateParameters: true);
            var publicKey = rsa.ExportSubjectPublicKeyInfo();
            return Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new ArgumentException("Private key is not a valid RSA PEM key.", ex);
        }
    }

    private static EnableBankingProfileView View(EnableBankingProfileDto profile) => new(
        profile.Id,
        profile.ApplicationId,
        profile.KeyFingerprint,
        profile.Environment,
        profile.ApplicationName,
        profile.Active,
        profile.Services,
        profile.RedirectUrls,
        profile.VerifiedAt,
        profile.UpdatedAt);

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> GetStrings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToArray()
            : [];

    private sealed record VerifiedApplication(
        string ApplicationName,
        string Environment,
        bool Active,
        IReadOnlyList<string> Services,
        IReadOnlyList<string> RedirectUrls);
}
