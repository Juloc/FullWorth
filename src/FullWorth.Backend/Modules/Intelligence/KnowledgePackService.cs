using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record KnowledgePackStatusView(
    bool Installed,
    string? PackId,
    string? Version,
    string? SchemaVersion,
    string? Region,
    int MerchantMappingCount,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? LastCheckedAt,
    string? LastErrorCode,
    IReadOnlyList<string> RollbackVersions);

public sealed record KnowledgePackApplyResult(
    string PackId,
    string Version,
    string Region,
    int MerchantMappingCount,
    bool RolledBack = false);

/// <summary>
/// Owns local Knowledge Pack activation. Verification happens before opening the write transaction;
/// active mappings are replaced atomically, so a failed verification/apply always leaves the previous
/// known-good installation intact.
/// </summary>
public sealed class KnowledgePackService(
    IntelligenceDbContext db,
    KnowledgePackVerifier verifier)
{
    public async Task<KnowledgePackStatusView> GetStatusAsync(CancellationToken ct)
    {
        var installed = await db.KnowledgePackInstallations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == KnowledgePackPolicy.InstallationScopeKey, ct);
        var rollback = await db.KnowledgePackArchives.AsNoTracking()
            .OrderByDescending(x => x.VerifiedAt)
            .Select(x => x.Version)
            .Take(KnowledgePackPolicy.ArchiveRetentionCount)
            .ToListAsync(ct);
        return new(
            installed is not null,
            installed?.PackId,
            installed?.Version,
            installed?.SchemaVersion,
            installed?.Region,
            installed?.MerchantMappingCount ?? 0,
            installed?.InstalledAt,
            installed?.LastCheckedAt,
            installed?.LastErrorCode,
            rollback);
    }

    public async Task<KnowledgePackApplyResult> InstallAsync(
        KnowledgePackManifest manifest,
        byte[] rawPayload,
        CancellationToken ct)
    {
        var verified = verifier.Verify(manifest, rawPayload);
        return await ApplyVerifiedAsync(verified, rolledBack: false, ct);
    }

    public async Task<KnowledgePackApplyResult> RollbackAsync(string packId, string version, CancellationToken ct)
    {
        var archive = await db.KnowledgePackArchives.AsNoTracking().SingleOrDefaultAsync(x =>
            x.PackId == packId && x.Version == version, ct)
            ?? throw new KeyNotFoundException("Knowledge Pack archive was not found.");

        byte[] raw;
        try { raw = Convert.FromBase64String(archive.PayloadBase64); }
        catch (FormatException ex)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_archive_corrupt", inner: ex);
        }

        var manifest = new KnowledgePackManifest(
            archive.PackId,
            archive.Version,
            archive.SchemaVersion,
            archive.Region,
            archive.ContentSha256,
            archive.SignatureAlgorithm,
            archive.SignatureBase64,
            null);
        var verified = verifier.Verify(manifest, raw);
        return await ApplyVerifiedAsync(verified, rolledBack: true, ct);
    }

    public async Task MarkCheckedAsync(string? errorCode, CancellationToken ct)
    {
        var installed = await db.KnowledgePackInstallations.SingleOrDefaultAsync(
            x => x.ScopeKey == KnowledgePackPolicy.InstallationScopeKey, ct);
        if (installed is null) return;
        installed.LastCheckedAt = DateTimeOffset.UtcNow;
        installed.LastErrorCode = Trim(errorCode, 120);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<OfficialMerchantCategoryMapping>> ListActiveCategoryMappingsAsync(CancellationToken ct) =>
        db.OfficialMerchantMappings.AsNoTracking()
            .Where(x => x.CategoryKey != null && x.Confidence > 0m)
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.AliasKey)
            .Select(x => new OfficialMerchantCategoryMapping(
                x.AliasKey,
                x.Direction,
                x.CategoryKey!,
                x.Confidence))
            .ToListAsync(ct);

    private async Task<KnowledgePackApplyResult> ApplyVerifiedAsync(
        VerifiedKnowledgePack verified,
        bool rolledBack,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // Full replacement of the installed pack: purge every stored row regardless of the
        // consent-gated query filter (matching the model's documented purge contract), otherwise a
        // reinstall while cloud is disabled deletes nothing and the reinsert hits the unique key.
        await db.OfficialMerchantMappings.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        if (verified.MerchantMappings.Count > 0)
            db.OfficialMerchantMappings.AddRange(verified.MerchantMappings);

        var installation = await db.KnowledgePackInstallations.SingleOrDefaultAsync(
            x => x.ScopeKey == KnowledgePackPolicy.InstallationScopeKey, ct);
        if (installation is null)
        {
            installation = new KnowledgePackInstallation { ScopeKey = KnowledgePackPolicy.InstallationScopeKey };
            db.KnowledgePackInstallations.Add(installation);
        }
        installation.PackId = verified.Manifest.PackId;
        installation.Version = verified.Manifest.Version;
        installation.SchemaVersion = verified.Manifest.SchemaVersion;
        installation.Region = verified.Manifest.Region;
        installation.ContentSha256 = verified.Manifest.ContentSha256;
        installation.SignatureAlgorithm = verified.Manifest.SignatureAlgorithm;
        installation.MerchantMappingCount = verified.MerchantMappings.Count;
        installation.InstalledAt = now;
        installation.LastCheckedAt = now;
        installation.LastErrorCode = null;

        var archive = await db.KnowledgePackArchives.SingleOrDefaultAsync(x =>
            x.PackId == verified.Manifest.PackId && x.Version == verified.Manifest.Version, ct);
        if (archive is null)
        {
            archive = new KnowledgePackArchive
            {
                PackId = verified.Manifest.PackId,
                Version = verified.Manifest.Version
            };
            db.KnowledgePackArchives.Add(archive);
        }
        archive.SchemaVersion = verified.Manifest.SchemaVersion;
        archive.Region = verified.Manifest.Region;
        archive.ContentSha256 = verified.Manifest.ContentSha256;
        archive.SignatureAlgorithm = verified.Manifest.SignatureAlgorithm;
        archive.SignatureBase64 = verified.Manifest.SignatureBase64;
        archive.PayloadBase64 = Convert.ToBase64String(verified.RawPayload);
        archive.VerifiedAt = now;

        await db.SaveChangesAsync(ct);
        await PruneArchivesAsync(verified.Manifest.PackId, verified.Manifest.Version, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new(
            verified.Manifest.PackId,
            verified.Manifest.Version,
            verified.Manifest.Region,
            verified.MerchantMappings.Count,
            rolledBack);
    }

    private async Task PruneArchivesAsync(string activePackId, string activeVersion, CancellationToken ct)
    {
        var archives = await db.KnowledgePackArchives
            .OrderByDescending(x => x.VerifiedAt)
            .ToListAsync(ct);
        var keepIds = archives
            .Take(KnowledgePackPolicy.ArchiveRetentionCount)
            .Select(x => x.Id)
            .ToHashSet();
        var active = archives.FirstOrDefault(x => x.PackId == activePackId && x.Version == activeVersion);
        if (active is not null) keepIds.Add(active.Id);
        foreach (var old in archives.Where(x => !keepIds.Contains(x.Id)))
            db.KnowledgePackArchives.Remove(old);
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
