using FullWorth.Banking.Backend;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.EnableBanking;

public sealed class EnableBankingProfileNotConfiguredException(string message) : InvalidOperationException(message);

/// <summary>
/// Resolves an Enable Banking API client from the FullWorth user's encrypted BYO profile. Existing
/// self-hosted installations may continue to use the legacy global ApplicationId/private-key config
/// for pre-migration connections that have no profile id.
/// </summary>
public sealed class EnableBankingClientResolver(
    IHttpClientFactory httpClientFactory,
    IOptions<EnableBankingOptions> options,
    EnableBankingRequestPolicy requestPolicy,
    FullWorthBackendClient backend)
{
    private readonly EnableBankingOptions _options = options.Value;

    public bool LegacyConfigured =>
        !string.IsNullOrWhiteSpace(_options.ApplicationId) &&
        !string.IsNullOrWhiteSpace(_options.RedirectUrl) &&
        (!string.IsNullOrWhiteSpace(_options.PrivateKeyBase64) ||
         (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath) && File.Exists(_options.PrivateKeyPath)));

    public Task<EnableBankingProfileDto?> GetProfileForUserAsync(Guid userId, CancellationToken ct) =>
        backend.GetEnableBankingProfileForUserAsync(userId, ct);

    public async Task<(EnableBankingClient Client, EnableBankingProfileDto? Profile)> ResolveForUserAsync(
        Guid userId,
        Guid? requestedProfileId,
        bool requireActive,
        CancellationToken ct)
    {
        EnableBankingProfileDto? profile = requestedProfileId is { } id
            ? await backend.GetEnableBankingProfileAsync(id, ct)
            : await backend.GetEnableBankingProfileForUserAsync(userId, ct);

        if (profile is not null)
        {
            if (profile.UserId != userId)
                throw new EnableBankingProfileNotConfiguredException("Enable Banking profile does not belong to the current user.");
            if (requireActive &&
                string.Equals(profile.Environment, "PRODUCTION", StringComparison.OrdinalIgnoreCase) &&
                !profile.Active)
                throw new EnableBankingProfileNotConfiguredException("Enable Banking production application is not active yet.");
            return (Create(profile.ApplicationId, profile.PrivateKeyPem), profile);
        }

        // Legacy/global credentials are intentionally NOT a fallback for new user-scoped
        // connections. They exist only so pre-BYO connections with no profile id keep syncing.
        throw new EnableBankingProfileNotConfiguredException(
            "No user-owned Enable Banking profile is configured. Legacy global credentials are only available to existing legacy connections.");
    }

    public async Task<EnableBankingClient> ResolveForConnectionAsync(BankConnectionDto connection, CancellationToken ct)
    {
        if (connection.EnableBankingProfileId is { } profileId)
        {
            var profile = await backend.GetEnableBankingProfileAsync(profileId, ct)
                ?? throw new EnableBankingProfileNotConfiguredException("Enable Banking profile for this connection no longer exists.");
            if (connection.AuthorizationUserId is not { } authorizationUserId ||
                authorizationUserId == Guid.Empty ||
                profile.UserId != authorizationUserId)
                throw new EnableBankingProfileNotConfiguredException(
                    "Enable Banking profile ownership does not match the connection authorization owner.");
            return Create(profile.ApplicationId, profile.PrivateKeyPem);
        }

        if (LegacyConfigured) return CreateLegacy();
        throw new EnableBankingProfileNotConfiguredException("This legacy bank connection has no available Enable Banking credentials.");
    }

    public EnableBankingClient CreateTemporary(string applicationId, string privateKeyPem) =>
        Create(applicationId, privateKeyPem);

    private EnableBankingClient Create(string applicationId, string privateKeyPem)
    {
        var http = httpClientFactory.CreateClient("enable-banking");
        return new EnableBankingClient(
            http,
            Options.Create(_options),
            requestPolicy,
            new EnableBankingCredentials(applicationId, privateKeyPem));
    }

    private EnableBankingClient CreateLegacy()
    {
        var http = httpClientFactory.CreateClient("enable-banking");
        return new EnableBankingClient(http, Options.Create(_options), requestPolicy);
    }
}
