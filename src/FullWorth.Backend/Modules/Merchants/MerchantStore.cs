using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FullWorth.Backend.Modules.Merchants;

public sealed class MerchantStore(FullWorthDbContext db)
{
    public async Task<(bool Found, IReadOnlyList<MerchantView>? Items)> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, null);
        var merchants = await db.Set<Merchant>().AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(x => x.Name)
            .Select(x => new MerchantView(
                x.Id, x.Name, x.NormalizedName,
                db.Set<MerchantAlias>().Where(a => a.MerchantId == x.Id)
                    .OrderBy(a => a.NormalizedAlias)
                    .Select(a => new MerchantAliasView(a.Id, a.NormalizedAlias)).ToList()))
            .ToListAsync(ct);
        return (true, merchants);
    }

    public async Task<MerchantOutcome<MerchantView>> CreateForUserAsync(Guid userId, Guid fullWorthSpaceId, MerchantWrite request, CancellationToken ct)
    {
        var role = await GetRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(MerchantResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(MerchantResult.Forbidden);

        var name = (request.Name ?? string.Empty).Trim();
        var normalized = MerchantNormalization.Normalize(name);
        if (name.Length == 0 || normalized is null) return new(MerchantResult.Invalid, Error: "Merchant name is required.");
        if (await db.Set<Merchant>().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.NormalizedName == normalized, ct))
            return new(MerchantResult.Invalid, Error: "A merchant with this name already exists.");

        var merchant = new Merchant { FullWorthSpaceId = fullWorthSpaceId, Name = name, NormalizedName = normalized };
        db.Set<Merchant>().Add(merchant);
        await db.SaveChangesAsync(ct);
        return new(MerchantResult.Success, new MerchantView(merchant.Id, merchant.Name, merchant.NormalizedName, []));
    }

    public async Task<MerchantOutcome<MerchantView>> RenameForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid merchantId, MerchantWrite request, CancellationToken ct)
    {
        var role = await GetRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(MerchantResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(MerchantResult.Forbidden);

        var merchant = await db.Set<Merchant>().SingleOrDefaultAsync(x => x.Id == merchantId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (merchant is null) return new(MerchantResult.NotFound);

        var name = (request.Name ?? string.Empty).Trim();
        var normalized = MerchantNormalization.Normalize(name);
        if (name.Length == 0 || normalized is null) return new(MerchantResult.Invalid, Error: "Merchant name is required.");
        // Duplicate check excludes this merchant, so a casing-only rename (same normalized value) is allowed.
        if (await db.Set<Merchant>().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.NormalizedName == normalized && x.Id != merchantId, ct))
            return new(MerchantResult.Invalid, Error: "A merchant with this name already exists.");

        merchant.Name = name;
        merchant.NormalizedName = normalized;
        await db.SaveChangesAsync(ct);
        return new(MerchantResult.Success, await BuildViewAsync(merchantId, ct));
    }

    // Fold the source merchant into the target: move the source's aliases onto the target, keep the
    // source's name resolvable by adding it as a target alias, then delete the source. Aliases are unique
    // per space, so moving them can never collide with the target's existing aliases.
    public async Task<MerchantOutcome<MerchantView>> MergeForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid targetId, MerchantMergeWrite request, CancellationToken ct)
    {
        var role = await GetRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(MerchantResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(MerchantResult.Forbidden);

        if (request.SourceMerchantId == targetId) return new(MerchantResult.Invalid, Error: "A merchant cannot be merged into itself.");

        var target = await db.Set<Merchant>().SingleOrDefaultAsync(x => x.Id == targetId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (target is null) return new(MerchantResult.NotFound);
        var source = await db.Set<Merchant>().SingleOrDefaultAsync(x => x.Id == request.SourceMerchantId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (source is null) return new(MerchantResult.NotFound);

        // Reassign the source's aliases to the target BEFORE deleting the source, so the FK cascade
        // (alias -> merchant) doesn't take them with it. EF applies the UPDATEs before the DELETE.
        var sourceAliases = await db.Set<MerchantAlias>().Where(a => a.MerchantId == source.Id && a.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);
        foreach (var a in sourceAliases) a.MerchantId = target.Id;

        // Preserve resolves that used to hit the source name, unless that value is already an alias in the space.
        var sourceNorm = source.NormalizedName;
        if (!await db.Set<MerchantAlias>().AnyAsync(a => a.FullWorthSpaceId == fullWorthSpaceId && a.NormalizedAlias == sourceNorm, ct))
            db.Set<MerchantAlias>().Add(new MerchantAlias { MerchantId = target.Id, FullWorthSpaceId = fullWorthSpaceId, NormalizedAlias = sourceNorm });

        db.Set<Merchant>().Remove(source);
        await db.SaveChangesAsync(ct);
        return new(MerchantResult.Success, await BuildViewAsync(target.Id, ct));
    }

    // Full MerchantView (with aliases) for a single merchant — matches the GET / list projection so
    // rename/merge responses carry the same shape.
    private Task<MerchantView> BuildViewAsync(Guid merchantId, CancellationToken ct) =>
        db.Set<Merchant>().AsNoTracking().Where(x => x.Id == merchantId)
            .Select(x => new MerchantView(x.Id, x.Name, x.NormalizedName,
                db.Set<MerchantAlias>().Where(a => a.MerchantId == x.Id)
                    .OrderBy(a => a.NormalizedAlias)
                    .Select(a => new MerchantAliasView(a.Id, a.NormalizedAlias)).ToList()))
            .SingleAsync(ct);

    public async Task<MerchantResult> DeleteForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid merchantId, CancellationToken ct)
    {
        var role = await GetRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return MerchantResult.NotFound;
        if (role != FullWorthSpaceRoles.Owner) return MerchantResult.Forbidden;

        var merchant = await db.Set<Merchant>().SingleOrDefaultAsync(x => x.Id == merchantId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (merchant is null) return MerchantResult.NotFound;
        db.Set<Merchant>().Remove(merchant);
        await db.SaveChangesAsync(ct);
        return MerchantResult.Success;
    }

    public async Task<MerchantOutcome<MerchantAliasView>> AddAliasForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid merchantId, AliasWrite request, CancellationToken ct)
    {
        var role = await GetRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return new(MerchantResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(MerchantResult.Forbidden);

        var merchantExists = await db.Set<Merchant>().AnyAsync(x => x.Id == merchantId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (!merchantExists) return new(MerchantResult.NotFound);

        var normalized = MerchantNormalization.Normalize(request.Alias);
        if (normalized is null) return new(MerchantResult.Invalid, Error: "Alias is required.");
        if (await db.Set<MerchantAlias>().AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.NormalizedAlias == normalized, ct))
            return new(MerchantResult.Invalid, Error: "This alias already maps to a merchant.");

        var alias = new MerchantAlias { MerchantId = merchantId, FullWorthSpaceId = fullWorthSpaceId, NormalizedAlias = normalized };
        db.Set<MerchantAlias>().Add(alias);
        await db.SaveChangesAsync(ct);
        return new(MerchantResult.Success, new MerchantAliasView(alias.Id, alias.NormalizedAlias));
    }

    public async Task<MerchantResult> RemoveAliasForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid merchantId, Guid aliasId, CancellationToken ct)
    {
        var role = await GetRoleAsync(userId, fullWorthSpaceId, ct);
        if (role is null) return MerchantResult.NotFound;
        if (role != FullWorthSpaceRoles.Owner) return MerchantResult.Forbidden;

        var alias = await db.Set<MerchantAlias>().SingleOrDefaultAsync(
            x => x.Id == aliasId && x.MerchantId == merchantId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (alias is null) return MerchantResult.NotFound;
        db.Set<MerchantAlias>().Remove(alias);
        await db.SaveChangesAsync(ct);
        return MerchantResult.Success;
    }

    public async Task<(bool Found, ResolveView? View)> ResolveForUserAsync(Guid userId, Guid fullWorthSpaceId, string? counterparty, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (false, null);
        var normalized = MerchantNormalization.Normalize(counterparty);
        if (normalized is null) return (true, new ResolveView(null, null, null));

        var aliases = await db.Set<MerchantAlias>().AsNoTracking()
            .Where(a => a.FullWorthSpaceId == fullWorthSpaceId)
            .Select(a => new { a.NormalizedAlias, a.MerchantId })
            .ToListAsync(ct);

        // Most specific (longest) alias contained in the counterparty wins.
        var match = aliases
            .Where(a => normalized.Contains(a.NormalizedAlias, StringComparison.Ordinal))
            .OrderByDescending(a => a.NormalizedAlias.Length)
            .FirstOrDefault();
        if (match is null) return (true, new ResolveView(normalized, null, null));

        var merchant = await db.Set<Merchant>().AsNoTracking()
            .Where(m => m.Id == match.MerchantId)
            .Select(m => new { m.Id, m.Name })
            .SingleAsync(ct);
        return (true, new ResolveView(normalized, merchant.Id, merchant.Name));
    }

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    private Task<string?> GetRoleAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => x.Role)
            .SingleOrDefaultAsync(ct);
}
