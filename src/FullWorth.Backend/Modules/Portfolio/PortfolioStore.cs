using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class PortfolioStore(FullWorthDbContext db, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public Task<List<Asset>> AssetsAsync(CancellationToken ct) => AssetsForSpaceAsync(FullWorthSpaceDefaults.LegacyId, ct);
    public Task<List<Liability>> LiabilitiesAsync(CancellationToken ct) => LiabilitiesForSpaceAsync(FullWorthSpaceDefaults.LegacyId, ct);
    public Task<List<NetWorthSnapshot>> HistoryAsync(DateOnly? from, DateOnly? to, CancellationToken ct) => LegacyHistoryAsync(from, to, ct);

    public Task<List<Asset>> AssetsForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Assets.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId).OrderBy(x => x.Name).ToListAsync(ct);

    public Task<List<Liability>> LiabilitiesForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Liabilities.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId).OrderBy(x => x.Name).ToListAsync(ct);

    public Task<List<NetWorthSnapshot>> HistoryForUserAsync(Guid fullWorthSpaceId, Guid userId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var query = db.NetWorthSnapshots.AsNoTracking().Where(snapshot =>
            snapshot.FullWorthSpaceId == fullWorthSpaceId &&
            snapshot.UserId == userId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));
        if (from.HasValue) query = query.Where(snapshot => snapshot.Date >= from.Value);
        if (to.HasValue) query = query.Where(snapshot => snapshot.Date <= to.Value);
        return query.OrderBy(snapshot => snapshot.Date).ToListAsync(ct);
    }

    public Task<List<AssetView>> AssetsForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        ProjectAssets(VisibleAssets(userId, fullWorthSpaceId).OrderBy(asset => asset.Name)).ToListAsync(ct);

    public Task<AssetView?> GetAssetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        ProjectAssets(VisibleAssets(userId, fullWorthSpaceId).Where(asset => asset.Id == assetId)).SingleOrDefaultAsync(ct);

    public Task<List<LiabilityView>> LiabilitiesForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        ProjectLiabilities(VisibleLiabilities(userId, fullWorthSpaceId).OrderBy(liability => liability.Name)).ToListAsync(ct);

    public Task<LiabilityView?> GetLiabilityForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid liabilityId, CancellationToken ct) =>
        ProjectLiabilities(VisibleLiabilities(userId, fullWorthSpaceId).Where(liability => liability.Id == liabilityId)).SingleOrDefaultAsync(ct);

    public Task<List<NetWorthSnapshotView>> HistoryViewForUserAsync(Guid fullWorthSpaceId, Guid userId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var query = db.NetWorthSnapshots.AsNoTracking().Where(snapshot =>
            snapshot.FullWorthSpaceId == fullWorthSpaceId &&
            snapshot.UserId == userId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));
        if (from.HasValue) query = query.Where(snapshot => snapshot.Date >= from.Value);
        if (to.HasValue) query = query.Where(snapshot => snapshot.Date <= to.Value);
        return query.OrderBy(snapshot => snapshot.Date)
            .Select(snapshot => new NetWorthSnapshotView(
                snapshot.Id,
                snapshot.FullWorthSpaceId,
                snapshot.Date,
                snapshot.Currency,
                snapshot.Accounts,
                snapshot.Assets,
                snapshot.Liabilities,
                snapshot.NetWorth,
                snapshot.CreatedAt))
            .ToListAsync(ct);
    }

    public Task<Asset> UpsertAssetAsync(Guid? id, AssetWrite request, CancellationToken ct) =>
        UpsertAssetForSpaceAsync(FullWorthSpaceDefaults.LegacyId, id, request, ct);

    public async Task<Asset> UpsertAssetForSpaceAsync(Guid fullWorthSpaceId, Guid? id, AssetWrite request, CancellationToken ct)
    {
        var entity = id.HasValue ? await db.Assets.SingleOrDefaultAsync(x => x.Id == id.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct) : null;
        if (id.HasValue && entity is null) throw new InvalidOperationException("Asset not found in FullWorth Space.");
        if (entity is null) { entity = new Asset { FullWorthSpaceId = fullWorthSpaceId }; db.Assets.Add(entity); }
        ApplyAssetWrite(entity, request);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public Task<Liability> UpsertLiabilityAsync(Guid? id, LiabilityWrite request, CancellationToken ct) =>
        UpsertLiabilityForSpaceAsync(FullWorthSpaceDefaults.LegacyId, id, request, ct);

    public async Task<Liability> UpsertLiabilityForSpaceAsync(Guid fullWorthSpaceId, Guid? id, LiabilityWrite request, CancellationToken ct)
    {
        var entity = id.HasValue ? await db.Liabilities.SingleOrDefaultAsync(x => x.Id == id.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct) : null;
        if (id.HasValue && entity is null) throw new InvalidOperationException("Liability not found in FullWorth Space.");
        if (entity is null) { entity = new Liability { FullWorthSpaceId = fullWorthSpaceId }; db.Liabilities.Add(entity); }
        ApplyLiabilityWrite(entity, request);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<AssetMutationOutcome> CreateAssetForUserAsync(Guid userId, Guid fullWorthSpaceId, AssetWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(PortfolioMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(PortfolioMutationResult.Forbidden);
        var error = ValidateAssetWrite(request);
        if (error is not null) return new(PortfolioMutationResult.Invalid, Error: error);

        var entity = new Asset { FullWorthSpaceId = fullWorthSpaceId };
        ApplyAssetWrite(entity, request);
        db.Assets.Add(entity);
        audit.Record(fullWorthSpaceId, userId, "asset.created", "Asset", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(PortfolioMutationResult.Success, await GetAssetForUserAsync(userId, fullWorthSpaceId, entity.Id, ct));
    }

    public async Task<AssetMutationOutcome> UpdateAssetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, AssetWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(PortfolioMutationResult.NotFound);
        if (!await VisibleAssets(userId, fullWorthSpaceId).AnyAsync(asset => asset.Id == assetId, ct)) return new(PortfolioMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(PortfolioMutationResult.Forbidden);
        var error = ValidateAssetWrite(request);
        if (error is not null) return new(PortfolioMutationResult.Invalid, Error: error);

        var entity = await WritableAssets(userId, fullWorthSpaceId).SingleOrDefaultAsync(asset => asset.Id == assetId, ct);
        if (entity is null) return new(PortfolioMutationResult.NotFound);
        ApplyAssetWrite(entity, request);
        audit.Record(fullWorthSpaceId, userId, "asset.updated", "Asset", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(PortfolioMutationResult.Success, await GetAssetForUserAsync(userId, fullWorthSpaceId, assetId, ct));
    }

    /// <summary>
    /// Loescht einen Vermoegenswert samt allem, was an ihm haengt.
    ///
    /// Das Aufraeumen macht die Datenbank: jede Detailtabelle (Immobilie, Fahrzeug, Edelmetall,
    /// Forderung, Sammlerstueck, Beteiligung, Versicherung, Bewertungen, Belege, Zahlungsstroeme,
    /// Schuldverknuepfungen, Vertragsverknuepfungen) haengt mit ON DELETE CASCADE daran. Ein
    /// bAV-Vertrag ueberlebt und verliert nur die Verknuepfung (ON DELETE SET NULL) - er ist ein
    /// eigener Vertrag, kein Teil des Vermoegenswerts.
    ///
    /// Die Vermoegenshistorie bleibt unangetastet: NetWorthSnapshots speichert Summen je Tag, keine
    /// einzelnen Werte. Was gestern galt, galt gestern.
    /// </summary>
    public async Task<PortfolioMutationResult> DeleteAssetForUserAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return PortfolioMutationResult.NotFound;
        if (!await VisibleAssets(userId, fullWorthSpaceId).AnyAsync(asset => asset.Id == assetId, ct))
            return PortfolioMutationResult.NotFound;
        if (role != FullWorthSpaceRoles.Owner) return PortfolioMutationResult.Forbidden;

        var entity = await WritableAssets(userId, fullWorthSpaceId).SingleOrDefaultAsync(asset => asset.Id == assetId, ct);
        if (entity is null) return PortfolioMutationResult.NotFound;

        db.Assets.Remove(entity);
        audit.Record(fullWorthSpaceId, userId, "asset.deleted", "Asset", entity.Id);
        await db.SaveChangesAsync(ct);
        return PortfolioMutationResult.Success;
    }

    /// <summary>
    /// Loescht eine Verbindlichkeit. Eine Verknuepfung zu einem Vermoegenswert (AssetDebtLinks)
    /// faellt mit, der Vermoegenswert selbst bleibt.
    /// </summary>
    public async Task<PortfolioMutationResult> DeleteLiabilityForUserAsync(
        Guid userId, Guid fullWorthSpaceId, Guid liabilityId, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return PortfolioMutationResult.NotFound;
        if (!await VisibleLiabilities(userId, fullWorthSpaceId).AnyAsync(liability => liability.Id == liabilityId, ct))
            return PortfolioMutationResult.NotFound;
        if (role != FullWorthSpaceRoles.Owner) return PortfolioMutationResult.Forbidden;

        var entity = await WritableLiabilities(userId, fullWorthSpaceId).SingleOrDefaultAsync(liability => liability.Id == liabilityId, ct);
        if (entity is null) return PortfolioMutationResult.NotFound;

        db.Liabilities.Remove(entity);
        audit.Record(fullWorthSpaceId, userId, "liability.deleted", "Liability", entity.Id);
        await db.SaveChangesAsync(ct);
        return PortfolioMutationResult.Success;
    }

    public async Task<LiabilityMutationOutcome> CreateLiabilityForUserAsync(Guid userId, Guid fullWorthSpaceId, LiabilityWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(PortfolioMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(PortfolioMutationResult.Forbidden);
        var error = ValidateLiabilityWrite(request);
        if (error is not null) return new(PortfolioMutationResult.Invalid, Error: error);

        var entity = new Liability { FullWorthSpaceId = fullWorthSpaceId };
        ApplyLiabilityWrite(entity, request);
        db.Liabilities.Add(entity);
        audit.Record(fullWorthSpaceId, userId, "liability.created", "Liability", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(PortfolioMutationResult.Success, await GetLiabilityForUserAsync(userId, fullWorthSpaceId, entity.Id, ct));
    }

    public async Task<LiabilityMutationOutcome> UpdateLiabilityForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid liabilityId, LiabilityWrite request, CancellationToken ct)
    {
        var role = await GetSpaceRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(PortfolioMutationResult.NotFound);
        if (!await VisibleLiabilities(userId, fullWorthSpaceId).AnyAsync(liability => liability.Id == liabilityId, ct)) return new(PortfolioMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(PortfolioMutationResult.Forbidden);
        var error = ValidateLiabilityWrite(request);
        if (error is not null) return new(PortfolioMutationResult.Invalid, Error: error);

        var entity = await WritableLiabilities(userId, fullWorthSpaceId).SingleOrDefaultAsync(liability => liability.Id == liabilityId, ct);
        if (entity is null) return new(PortfolioMutationResult.NotFound);
        ApplyLiabilityWrite(entity, request);
        audit.Record(fullWorthSpaceId, userId, "liability.updated", "Liability", entity.Id);
        await db.SaveChangesAsync(ct);
        return new(PortfolioMutationResult.Success, await GetLiabilityForUserAsync(userId, fullWorthSpaceId, liabilityId, ct));
    }

    private Task<List<NetWorthSnapshot>> LegacyHistoryAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var query = db.NetWorthSnapshots.AsNoTracking().Where(x => x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId && x.UserId == null);
        if (from.HasValue) query = query.Where(x => x.Date >= from.Value);
        if (to.HasValue) query = query.Where(x => x.Date <= to.Value);
        return query.OrderBy(x => x.Date).ToListAsync(ct);
    }

    private IQueryable<Asset> VisibleAssets(Guid userId, Guid fullWorthSpaceId) =>
        db.Assets.AsNoTracking().Where(asset =>
            asset.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));

    private IQueryable<Asset> WritableAssets(Guid userId, Guid fullWorthSpaceId) =>
        db.Assets.Where(asset =>
            asset.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner));

    private IQueryable<Liability> VisibleLiabilities(Guid userId, Guid fullWorthSpaceId) =>
        db.Liabilities.AsNoTracking().Where(liability =>
            liability.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));

    private IQueryable<Liability> WritableLiabilities(Guid userId, Guid fullWorthSpaceId) =>
        db.Liabilities.Where(liability =>
            liability.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner));

    private IQueryable<AssetView> ProjectAssets(IQueryable<Asset> assets) =>
        assets.Select(asset => new AssetView(
            asset.Id, asset.FullWorthSpaceId, asset.Name, asset.Kind, asset.CurrentValue, asset.Currency,
            asset.ValuedAt, asset.ValueRecordedAt, asset.AnnualGrowthRate, asset.IncludeInNetWorth, asset.Notes,
            asset.CreatedAt, asset.UpdatedAt));

    private IQueryable<LiabilityView> ProjectLiabilities(IQueryable<Liability> liabilities) =>
        liabilities.Select(liability => new LiabilityView(
            liability.Id, liability.FullWorthSpaceId, liability.Name, liability.Kind, liability.CurrentBalance, liability.Currency,
            liability.InterestRate, liability.RegularPayment, liability.PaymentCycle, liability.NextDueDate, liability.EndDate,
            liability.IncludeInNetWorth, liability.Notes, liability.CreatedAt, liability.UpdatedAt));

    private Task<string?> GetSpaceRoleAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);

    private static string? ValidateAssetWrite(AssetWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Asset name is required.";
        return ValidateCurrency(request.Currency);
    }

    private static string? ValidateLiabilityWrite(LiabilityWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "Liability name is required.";
        if (string.IsNullOrWhiteSpace(request.PaymentCycle)) return "Payment cycle is required.";
        return ValidateCurrency(request.Currency);
    }

    private static string? ValidateCurrency(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency)) return "Currency is required.";
        var normalized = currency.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(character => character is >= 'A' and <= 'Z')
            ? null
            : "Currency must be a three-letter code.";
    }

    private static void ApplyAssetWrite(Asset entity, AssetWrite request)
    {
        entity.Name = request.Name.Trim();
        entity.Kind = string.IsNullOrWhiteSpace(request.Kind) ? "other" : request.Kind.Trim().ToLowerInvariant();
        entity.CurrentValue = request.CurrentValue;
        entity.Currency = request.Currency.Trim().ToUpperInvariant();
        // A stated date only. No date means no date - ValueRecordedAt is stamped by the database, so a
        // caller can neither omit nor forge "since when do we know this".
        entity.ValuedAt = request.ValuedAt;
        entity.AnnualGrowthRate = request.AnnualGrowthRate;
        entity.IncludeInNetWorth = request.IncludeInNetWorth;
        entity.Notes = request.Notes?.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ApplyLiabilityWrite(Liability entity, LiabilityWrite request)
    {
        entity.Name = request.Name.Trim();
        entity.Kind = string.IsNullOrWhiteSpace(request.Kind) ? "loan" : request.Kind.Trim().ToLowerInvariant();
        entity.CurrentBalance = request.CurrentBalance;
        entity.Currency = request.Currency.Trim().ToUpperInvariant();
        entity.InterestRate = request.InterestRate;
        entity.RegularPayment = request.RegularPayment;
        entity.PaymentCycle = request.PaymentCycle.Trim().ToLowerInvariant();
        entity.NextDueDate = request.NextDueDate;
        entity.EndDate = request.EndDate;
        entity.IncludeInNetWorth = request.IncludeInNetWorth;
        entity.Notes = request.Notes?.Trim();
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
