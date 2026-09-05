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
        File.Exists(_options.PrivateKeyPath);

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
            if (requireActive && !profile.Active)
                throw new EnableBankingProfileNotConfiguredException("Enable Banking production application is not active yet.");
            return (Create(profile.ApplicationId, profile.PrivateKeyPem), profile);
        }

        if (LegacyConfigured)
            return (CreateLegacy(), null);

        throw new EnableBankingProfileNotConfiguredException("No Enable Banking profile is configured for this user.");
    }

    public async Task<EnableBankingClient> ResolveForConnectionAsync(BankConnectionDto connection, CancellationToken ct)
    {
        if (connection.EnableBankingProfileId is { } profileId)
        {
            var profile = await backend.GetEnableBankingProfileAsync(profileId, ct)
                ?? throw new EnableBankingProfileNotConfiguredException("Enable Banking profile for this connection no longer exists.");
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
