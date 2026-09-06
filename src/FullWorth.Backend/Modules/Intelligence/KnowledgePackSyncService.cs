using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record KnowledgePackSyncResult(
    string Status,
    string? Version,
    int MerchantMappings,
    string? ErrorCode);

public sealed class KnowledgePackSyncService(
    IntelligenceDbContext db,
    CloudIntelligenceStateService stateService,
    CloudInstanceCredentialStore credentialStore,
    IFullWorthCloudClient cloud,
    IConfiguration configuration,
    ILogger<KnowledgePackSyncService> logger)
{
    public const int MaximumPackBytes = 5 * 1024 * 1024;
    private const int ArchiveRetention = 3;

    public async Task<KnowledgePackSyncResult> SyncOnceAsync(CancellationToken ct)
    {
        if (!await stateService.HasCurrentActiveConsentAsync(ct))
            return new("disabled", null, 0, null);

        var state = await stateService.GetEnabledStateAsync(ct);
        if (state is null)
            return new("disabled", null, 0, null);

        var installation = await db.KnowledgePackInstallations.SingleOrDefaultAsync(
            x => x.ScopeKey == KnowledgePackProtocol.InstallationScopeKey, ct);
        var now = DateTimeOffset.UtcNow;

        try
        {
            var secret = await EnsureCredentialAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
                return await FailAsync(installation, state, "cloud_credential_missing", ct);

            var region = NormalizeRegion(configuration["FullWorthCloud:KnowledgePackRegion"]);
            var manifest = await cloud.GetLatestKnowledgePackManifestAsync(
                secret,
                installation?.Version,
                region,
                ct);

            state.LastKnowledgePackCheckAt = now;
            state.UpdatedAt = now;

            if (manifest is null)
            {
                if (installation is not null)
                {
                    installation.LastCheckedAt = now;
                    installation.LastErrorCode = null;
                }
                await db.SaveChangesAsync(ct);
                return new("current", installation?.Version, installation?.MerchantMappingCount ?? 0, null);
            }

            ValidateManifest(manifest, region);

            var payloadBytes = await cloud.DownloadKnowledgePackAsync(
                secret,
                manifest.PackId,
                manifest.Version,
                ct);
            if (payloadBytes.Length is <= 0 or > MaximumPackBytes)
                throw new KnowledgePackVerificationException("knowledge_pack_size_invalid");

            VerifyPayloadBytes(manifest, payloadBytes);
            var payload = DeserializeAndValidatePayload(manifest, payloadBytes);

            var mappings = payload.Merchants
                .Where(x => !string.IsNullOrWhiteSpace(x.CategoryKey))
                .Select(x => ToEntity(manifest, x))
                .GroupBy(x => new { x.AliasKey, x.Direction, x.Country })
                .Select(g => g.OrderByDescending(x => x.Confidence).First())
                .ToList();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var existingMappings = await db.OfficialMerchantMappings.ToListAsync(ct);
            db.OfficialMerchantMappings.RemoveRange(existingMappings);
            db.OfficialMerchantMappings.AddRange(mappings);

            installation ??= new KnowledgePackInstallation
            {
                ScopeKey = KnowledgePackProtocol.InstallationScopeKey
            };
            if (db.Entry(installation).State == EntityState.Detached)
                db.KnowledgePackInstallations.Add(installation);

            installation.PackId = manifest.PackId;
            installation.Version = manifest.Version;
            installation.SchemaVersion = manifest.SchemaVersion;
            installation.Region = manifest.Region;
            installation.ContentSha256 = manifest.ContentSha256.ToLowerInvariant();
            installation.SignatureAlgorithm = manifest.SignatureAlgorithm;
            installation.MerchantMappingCount = mappings.Count;
            installation.InstalledAt = now;
            installation.LastCheckedAt = now;
            installation.LastErrorCode = null;

            if (!await db.KnowledgePackArchives.AnyAsync(
                    x => x.PackId == manifest.PackId && x.Version == manifest.Version, ct))
            {
                db.KnowledgePackArchives.Add(new KnowledgePackArchive
                {
                    PackId = manifest.PackId,
                    Version = manifest.Version,
                    SchemaVersion = manifest.SchemaVersion,
                    Region = manifest.Region,
                    ContentSha256 = manifest.ContentSha256.ToLowerInvariant(),
                    SignatureAlgorithm = manifest.SignatureAlgorithm,
                    SignatureBase64 = manifest.SignatureBase64,
                    PayloadBase64 = Convert.ToBase64String(payloadBytes),
                    VerifiedAt = now
                });
            }

            state.LastKnowledgePackCheckAt = now;
            state.LastErrorCode = null;
            state.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            await PruneArchivesAsync(ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            await credentialStore.MarkUsedAsync(state.InstanceId, ct);
            return new("installed", manifest.Version, mappings.Count, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (KnowledgePackVerificationException ex)
        {
            logger.LogWarning("FullWorth Cloud knowledge pack rejected: {ErrorCode}", ex.ErrorCode);
            return await FailAsync(installation, state, ex.ErrorCode, ct);
        }
        catch (FullWorthCloudException ex)
        {
            logger.LogWarning("FullWorth Cloud knowledge-pack sync failed: {ErrorCode}", ex.ErrorCode);
            if (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                await credentialStore.DeleteAsync(state.InstanceId, CancellationToken.None);
            return await FailAsync(installation, state, ex.ErrorCode, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FullWorth Cloud knowledge-pack sync failed; previous verified pack stays active.");
            return await FailAsync(installation, state, "knowledge_pack_sync_failed", CancellationToken.None);
        }
    }

    private async Task<string?> EnsureCredentialAsync(Guid instanceId, CancellationToken ct)
    {
        var secret = await credentialStore.GetSecretAsync(instanceId, ct);
        if (!string.IsNullOrWhiteSpace(secret)) return secret;

        var registration = await cloud.RegisterAsync(
            instanceId,
            CloudIntelligencePolicy.CurrentVersion,
            typeof(KnowledgePackSyncService).Assembly.GetName().Version?.ToString() ?? "unknown",
            ct);
        await credentialStore.SaveAsync(registration, ct);
        await stateService.SetTransportStatusAsync(
            instanceId,
            null,
            registration.EntitlementStatus,
            DateTimeOffset.UtcNow,
            null,
            ct);
        return registration.Credential;
    }

    private void ValidateManifest(KnowledgePackManifest manifest, string expectedRegion)
    {
        if (string.IsNullOrWhiteSpace(manifest.PackId) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            !string.Equals(manifest.SchemaVersion, KnowledgePackProtocol.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.SignatureAlgorithm, KnowledgePackProtocol.SignatureAlgorithm, StringComparison.Ordinal) ||
            !string.Equals(NormalizeRegion(manifest.Region), expectedRegion, StringComparison.Ordinal) ||
            manifest.ContentSha256.Length != 64 ||
            !manifest.ContentSha256.All(Uri.IsHexDigit))
            throw new KnowledgePackVerificationException("knowledge_pack_manifest_invalid");

        var expectedPackId = configuration["FullWorthCloud:KnowledgePackId"]?.Trim();
        if (!string.IsNullOrWhiteSpace(expectedPackId) &&
            !string.Equals(manifest.PackId, expectedPackId, StringComparison.Ordinal))
            throw new KnowledgePackVerificationException("knowledge_pack_id_untrusted");

        if (!string.IsNullOrWhiteSpace(manifest.MinimumClientVersion) &&
            Version.TryParse(manifest.MinimumClientVersion, out var minimum) &&
            Version.TryParse(typeof(KnowledgePackSyncService).Assembly.GetName().Version?.ToString(), out var current) &&
            current < minimum)
            throw new KnowledgePackVerificationException("knowledge_pack_client_too_old");
    }

    private void VerifyPayloadBytes(KnowledgePackManifest manifest, byte[] payload)
    {
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hash),
                Encoding.ASCII.GetBytes(manifest.ContentSha256.ToLowerInvariant())))
            throw new KnowledgePackVerificationException("knowledge_pack_hash_mismatch");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.SignatureBase64);
        }
        catch (FormatException)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_signature_invalid");
        }

        var publicKeyPem = ResolvePublicKeyPem();
        if (string.IsNullOrWhiteSpace(publicKeyPem))
            throw new KnowledgePackVerificationException("knowledge_pack_public_key_missing");

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(publicKeyPem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_public_key_invalid");
        }

        if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new KnowledgePackVerificationException("knowledge_pack_signature_invalid");
    }

    private static KnowledgePackPayload DeserializeAndValidatePayload(
        KnowledgePackManifest manifest,
        byte[] payloadBytes)
    {
        KnowledgePackPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<KnowledgePackPayload>(
                          payloadBytes,
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new JsonException("Empty knowledge pack.");
        }
        catch (JsonException)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_payload_invalid");
        }

        if (!string.Equals(payload.PackId, manifest.PackId, StringComparison.Ordinal) ||
            !string.Equals(payload.Version, manifest.Version, StringComparison.Ordinal) ||
            !string.Equals(payload.SchemaVersion, manifest.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(NormalizeRegion(payload.Region), NormalizeRegion(manifest.Region), StringComparison.Ordinal) ||
            payload.Merchants is null ||
            payload.Merchants.Count > 100_000)
            throw new KnowledgePackVerificationException("knowledge_pack_payload_invalid");

        return payload;
    }

    private static OfficialMerchantMapping ToEntity(KnowledgePackManifest manifest, KnowledgePackMerchantPayload source)
    {
        var aliasKey = source.AliasKey;
        var direction = NormalizeDirection(source.Direction);
        var country = string.IsNullOrWhiteSpace(source.Country) ? "GLOBAL" : source.Country.Trim().ToUpperInvariant();

        // Compatibility with packs produced before 2026-09-06 where AliasKey could contain the internal
        // alias+direction+country composite representation.
        const char separator = '';
        if (aliasKey.Contains(separator))
        {
            var parts = aliasKey.Split(separator);
            aliasKey = parts.ElementAtOrDefault(0) ?? aliasKey;
            direction = NormalizeDirection(parts.ElementAtOrDefault(1));
            var legacyCountry = parts.ElementAtOrDefault(2);
            if (!string.IsNullOrWhiteSpace(legacyCountry))
                country = legacyCountry.Trim().ToUpperInvariant();
        }

        var normalizedAlias = MerchantNormalization.Normalize(aliasKey)
            ?? throw new KnowledgePackVerificationException("knowledge_pack_merchant_invalid");
        if (string.IsNullOrWhiteSpace(source.CanonicalMerchantKey) ||
            string.IsNullOrWhiteSpace(source.CanonicalName) ||
            string.IsNullOrWhiteSpace(source.CategoryKey) ||
            source.Confidence is < 0m or > 1m)
            throw new KnowledgePackVerificationException("knowledge_pack_merchant_invalid");

        return new OfficialMerchantMapping
        {
            PackId = manifest.PackId,
            PackVersion = manifest.Version,
            AliasKey = normalizedAlias,
            Direction = direction,
            CanonicalMerchantKey = source.CanonicalMerchantKey.Trim(),
            CanonicalName = source.CanonicalName.Trim(),
            CategoryKey = source.CategoryKey.Trim(),
            Country = country,
            Confidence = source.Confidence,
            Domain = Trim(source.Domain, 255),
            LogoKey = Trim(source.LogoKey, 180)
        };
    }

    private async Task<KnowledgePackSyncResult> FailAsync(
        KnowledgePackInstallation? installation,
        CloudConnectionState state,
        string errorCode,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (installation is not null)
        {
            installation.LastCheckedAt = now;
            installation.LastErrorCode = Trim(errorCode, 120);
        }
        state.LastKnowledgePackCheckAt = now;
        state.LastErrorCode = Trim(errorCode, 120);
        state.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new(
            "failed",
            installation?.Version,
            installation?.MerchantMappingCount ?? 0,
            errorCode);
    }

    private async Task PruneArchivesAsync(CancellationToken ct)
    {
        var old = await db.KnowledgePackArchives
            .OrderByDescending(x => x.VerifiedAt)
            .Skip(ArchiveRetention)
            .ToListAsync(ct);
        if (old.Count > 0)
            db.KnowledgePackArchives.RemoveRange(old);
    }

    private string? ResolvePublicKeyPem()
    {
        var pem = configuration["FullWorthCloud:KnowledgePackPublicKeyPem"];
        if (!string.IsNullOrWhiteSpace(pem))
            return pem.Replace("\\n", "
", StringComparison.Ordinal);

        var encoded = configuration["FullWorthCloud:KnowledgePackPublicKeyBase64"];
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string NormalizeRegion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "GLOBAL" : value.Trim().ToUpperInvariant();

    private static string NormalizeDirection(string? value) =>
        value?.Trim().ToLowerInvariant() is "income" or "expense" ? value.Trim().ToLowerInvariant() : "any";

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public sealed class KnowledgePackVerificationException(string errorCode) : Exception(errorCode)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class KnowledgePackSyncWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<KnowledgePackSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<KnowledgePackSyncService>()
                    .SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FullWorth knowledge-pack sync worker iteration failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
