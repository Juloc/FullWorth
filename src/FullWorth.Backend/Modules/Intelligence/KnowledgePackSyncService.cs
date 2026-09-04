using System.Net;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record KnowledgePackSyncResult(
    string Mode,
    bool Checked,
    bool Updated,
    bool Registered,
    string? Version,
    int MerchantMappingCount,
    string? ErrorCode = null);

/// <summary>
/// Downloads official FullWorth Knowledge Packs only for instances with current reciprocal Cloud
/// Intelligence consent. The consent gate is evaluated before credential issuance or any HTTP request.
/// </summary>
public sealed class KnowledgePackSyncService(
    IntelligenceDbContext db,
    CloudInstanceCredentialStore credentials,
    IFullWorthCloudClient cloud,
    IFullWorthKnowledgePackClient packs,
    KnowledgePackService installer,
    IConfiguration configuration)
{
    public async Task<KnowledgePackSyncResult> SyncLatestAsync(CancellationToken ct)
    {
        var gate = await GetActiveGateAsync(ct);
        if (gate is null)
            return new(CloudIntelligenceModes.Disabled, false, false, false, null, 0);

        var state = gate.Value.State;
        var consent = gate.Value.Consent;
        var registered = false;
        try
        {
            var credential = await EnsureCredentialAsync(state, consent, ct);
            registered = credential.Registered;
            var current = await db.KnowledgePackInstallations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ScopeKey == KnowledgePackPolicy.InstallationScopeKey, ct);
            var region = NormalizeRegion(current?.Region ?? configuration["FullWorthCloud:KnowledgePackRegion"] ?? "DE");

            var manifestResult = await GetManifestWithAuthRecoveryAsync(state, credential.Secret, current?.Version, region, ct);
            if (manifestResult.Rotated) credential = (manifestResult.Secret, registered);
            var manifest = manifestResult.Manifest;
            if (manifest is null)
            {
                await installer.MarkCheckedAsync(null, ct);
                return new(state.Mode, true, false, registered, current?.Version, current?.MerchantMappingCount ?? 0);
            }

            EnsureClientCompatible(consent.ClientVersion, manifest.MinimumClientVersion);
            if (current is not null &&
                string.Equals(current.PackId, manifest.PackId, StringComparison.Ordinal) &&
                string.Equals(current.Version, manifest.Version, StringComparison.Ordinal) &&
                string.Equals(current.ContentSha256, manifest.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                await installer.MarkCheckedAsync(null, ct);
                return new(state.Mode, true, false, registered, current.Version, current.MerchantMappingCount);
            }

            var download = await DownloadWithAuthRecoveryAsync(state, credential.Secret, manifest, ct);
            var applied = await installer.InstallAsync(manifest, download.Payload, ct);
            await credentials.MarkUsedAsync(state.InstanceId, ct);
            return new(state.Mode, true, true, registered, applied.Version, applied.MerchantMappingCount);
        }
        catch (KnowledgePackVerificationException ex)
        {
            await installer.MarkCheckedAsync(ex.ErrorCode, ct);
            return new(state.Mode, true, false, registered, null, 0, ex.ErrorCode);
        }
        catch (FullWorthCloudException ex)
        {
            await installer.MarkCheckedAsync(ex.ErrorCode, ct);
            return new(state.Mode, true, false, registered, null, 0, ex.ErrorCode);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("knowledge_pack_", StringComparison.Ordinal))
        {
            await installer.MarkCheckedAsync(ex.Message, ct);
            return new(state.Mode, true, false, registered, null, 0, ex.Message);
        }
    }

    private async Task<(CloudConnectionState State, CloudIntelligenceConsent Consent)?> GetActiveGateAsync(CancellationToken ct)
    {
        var state = await db.CloudConnectionStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == CloudConnectionState.InstanceScopeKey, ct);
        if (state is null || state.Mode != CloudIntelligenceModes.Enabled) return null;

        var consent = await db.CloudIntelligenceConsents.AsNoTracking()
            .Where(x => x.InstanceId == state.InstanceId &&
                        x.PolicyVersion == CloudIntelligencePolicy.CurrentVersion &&
                        x.RevokedAt == null)
            .OrderByDescending(x => x.AcceptedAt)
            .FirstOrDefaultAsync(ct);
        return consent is null ? null : (state, consent);
    }

    private async Task<(string Secret, bool Registered)> EnsureCredentialAsync(
        CloudConnectionState state,
        CloudIntelligenceConsent consent,
        CancellationToken ct)
    {
        var row = await credentials.GetAsync(state.InstanceId, ct);
        var secret = row is null ? null : await credentials.GetSecretAsync(state.InstanceId, ct);
        FullWorthCloudRegistrationResult? issued = null;
        var registered = false;
        if (row is null || string.IsNullOrWhiteSpace(secret))
        {
            issued = await cloud.RegisterAsync(
                state.InstanceId,
                CloudIntelligencePolicy.CurrentVersion,
                string.IsNullOrWhiteSpace(consent.ClientVersion) ? "unknown" : consent.ClientVersion,
                ct);
            registered = true;
        }
        else if (row.ExpiresAt.HasValue && row.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddHours(24))
        {
            issued = await cloud.RotateCredentialAsync(state.InstanceId, secret, ct);
        }

        if (issued is not null)
        {
            await credentials.SaveAsync(issued, ct);
            var tracked = await db.CloudConnectionStates.SingleAsync(x => x.Id == state.Id, ct);
            tracked.LastRegistrationAt = DateTimeOffset.UtcNow;
            tracked.EntitlementStatus = issued.EntitlementStatus;
            tracked.LastErrorCode = null;
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            secret = issued.Credential;
        }
        return (secret!, registered);
    }

    private async Task<(KnowledgePackManifest? Manifest, string Secret, bool Rotated)> GetManifestWithAuthRecoveryAsync(
        CloudConnectionState state,
        string secret,
        string? currentVersion,
        string region,
        CancellationToken ct)
    {
        try
        {
            return (await packs.GetLatestManifestAsync(state.InstanceId, secret, currentVersion, region, ct), secret, false);
        }
        catch (FullWorthCloudException ex) when (IsUnauthorized(ex))
        {
            var rotated = await cloud.RotateCredentialAsync(state.InstanceId, secret, ct);
            await credentials.SaveAsync(rotated, ct);
            return (await packs.GetLatestManifestAsync(state.InstanceId, rotated.Credential, currentVersion, region, ct), rotated.Credential, true);
        }
    }

    private async Task<(byte[] Payload, string Secret)> DownloadWithAuthRecoveryAsync(
        CloudConnectionState state,
        string secret,
        KnowledgePackManifest manifest,
        CancellationToken ct)
    {
        try
        {
            return (await packs.DownloadPackAsync(state.InstanceId, secret, manifest.PackId, manifest.Version, ct), secret);
        }
        catch (FullWorthCloudException ex) when (IsUnauthorized(ex))
        {
            var rotated = await cloud.RotateCredentialAsync(state.InstanceId, secret, ct);
            await credentials.SaveAsync(rotated, ct);
            return (await packs.DownloadPackAsync(state.InstanceId, rotated.Credential, manifest.PackId, manifest.Version, ct), rotated.Credential);
        }
    }

    private static bool IsUnauthorized(FullWorthCloudException ex) =>
        ex.StatusCode == HttpStatusCode.Unauthorized || ex.ErrorCode == "cloud_unauthorized";

    private static string NormalizeRegion(string region)
    {
        var value = region.Trim().ToUpperInvariant();
        return value.Length is > 0 and <= 32 ? value : "DE";
    }

    internal static void EnsureClientCompatible(string? clientVersion, string? minimumClientVersion)
    {
        if (string.IsNullOrWhiteSpace(minimumClientVersion)) return;
        if (!TryParseVersion(minimumClientVersion, out var minimum))
            throw new InvalidOperationException("knowledge_pack_minimum_client_invalid");
        if (!TryParseVersion(clientVersion, out var current))
            throw new InvalidOperationException("knowledge_pack_client_version_unknown");
        if (current < minimum)
            throw new InvalidOperationException("knowledge_pack_client_too_old");
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;
        var core = value.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out version!);
    }
}

/// <summary>Low-frequency updater; disabled/local-only instances exit locally without network access.</summary>
public sealed class KnowledgePackSyncWorker(IServiceScopeFactory scopeFactory, ILogger<KnowledgePackSyncWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromHours(24);
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<KnowledgePackSyncService>()
                    .SyncLatestAsync(stoppingToken);
                if (result.ErrorCode is not null)
                {
                    logger.LogWarning("Knowledge Pack sync failed with {ErrorCode}.", result.ErrorCode);
                    delay = TimeSpan.FromHours(6);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Knowledge Pack sync failed unexpectedly.");
                delay = TimeSpan.FromHours(6);
            }

            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
