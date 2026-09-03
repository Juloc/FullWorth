using System.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed class FullWorthSpaceStore(DbContext db, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public Task<List<FullWorthSpace>> ListForUserAsync(Guid userId, CancellationToken ct)
    {
        ValidateUserId(userId, nameof(userId));
        return db.Set<FullWorthSpace>()
            .AsNoTracking()
            .Where(space => db.Set<FullWorthSpaceMember>().Any(member =>
                member.FullWorthSpaceId == space.Id && member.UserId == userId))
            .OrderBy(space => space.Name)
            .ThenBy(space => space.Id)
            .ToListAsync(ct);
    }

    public Task<FullWorthSpace?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        ValidateUserId(userId, nameof(userId));
        ValidateFullWorthSpaceId(fullWorthSpaceId);
        return db.Set<FullWorthSpace>()
            .AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Where(space => db.Set<FullWorthSpaceMember>().Any(member =>
                member.FullWorthSpaceId == space.Id && member.UserId == userId))
            .SingleOrDefaultAsync(ct);
    }

    public async Task<FullWorthSpace> CreateAsync(Guid ownerUserId, string name, string? baseCurrency, CancellationToken ct)
    {
        ValidateUserId(ownerUserId, nameof(ownerUserId));
        var now = DateTimeOffset.UtcNow;
        var space = new FullWorthSpace
        {
            Name = NormalizeName(name),
            BaseCurrency = NormalizeCurrency(baseCurrency),
            CreatedAt = now,
            UpdatedAt = now
        };
        var owner = new FullWorthSpaceMember
        {
            FullWorthSpaceId = space.Id,
            UserId = ownerUserId,
            Role = FullWorthSpaceRoles.Owner,
            JoinedAt = now,
            FullWorthSpace = space
        };

        db.Set<FullWorthSpace>().Add(space);
        db.Set<FullWorthSpaceMember>().Add(owner);
        await db.SaveChangesAsync(ct);
        return space;
    }

    public async Task<FullWorthSpaceMember> AddMemberAsync(Guid actingUserId, Guid fullWorthSpaceId, Guid userId, string role, CancellationToken ct)
    {
        ValidateUserId(actingUserId, nameof(actingUserId));
        ValidateFullWorthSpaceId(fullWorthSpaceId);
        ValidateUserId(userId, nameof(userId));
        ValidateRole(role);

        if (!await IsOwnerAsync(actingUserId, fullWorthSpaceId, ct))
        {
            throw new FullWorthSpaceNotFoundException();
        }

        var members = db.Set<FullWorthSpaceMember>();
        if (await members.AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct))
        {
            throw new FullWorthSpaceMembershipExistsException();
        }

        var member = new FullWorthSpaceMember
        {
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            Role = role,
            JoinedAt = DateTimeOffset.UtcNow
        };
        members.Add(member);
        audit.Record(fullWorthSpaceId, actingUserId, "space.member.added", "FullWorthSpaceMember", userId);
        await db.SaveChangesAsync(ct);
        return member;
    }

    public async Task RemoveMemberAsync(Guid actingUserId, Guid fullWorthSpaceId, Guid userId, CancellationToken ct)
    {
        ValidateUserId(actingUserId, nameof(actingUserId));
        ValidateFullWorthSpaceId(fullWorthSpaceId);
        ValidateUserId(userId, nameof(userId));

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (!await IsOwnerAsync(actingUserId, fullWorthSpaceId, ct))
        {
            throw new FullWorthSpaceNotFoundException();
        }

        var members = db.Set<FullWorthSpaceMember>();
        var member = await members.SingleOrDefaultAsync(item =>
            item.FullWorthSpaceId == fullWorthSpaceId && item.UserId == userId, ct);
        if (member is null)
        {
            throw new FullWorthSpaceNotFoundException();
        }

        if (member.Role == FullWorthSpaceRoles.Owner)
        {
            var ownerCount = await members.CountAsync(item =>
                item.FullWorthSpaceId == fullWorthSpaceId && item.Role == FullWorthSpaceRoles.Owner, ct);
            if (ownerCount <= 1)
            {
                throw new FullWorthSpaceLastOwnerException();
            }
        }

        members.Remove(member);

        // Full de-provision: also drop the member's per-account grants in THIS space, so a later re-add or
        // re-invite starts clean and cannot silently restore access the re-grant never included. Guarded by
        // a model check because this store is also used against a narrow context (FullWorthSpace/Member/User
        // only) in unit tests where AccountOwner is not mapped.
        if (db.Model.FindEntityType(typeof(AccountOwner)) is not null)
        {
            var staleGrants = await db.Set<AccountOwner>()
                .Where(owner => owner.UserId == userId
                    && db.Set<FinanceAccount>().Any(account => account.Id == owner.AccountId && account.FullWorthSpaceId == fullWorthSpaceId))
                .ToListAsync(ct);
            if (staleGrants.Count > 0) db.Set<AccountOwner>().RemoveRange(staleGrants);
        }

        audit.Record(fullWorthSpaceId, actingUserId, "space.member.removed", "FullWorthSpaceMember", userId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    // Any member may read the roster (needed by the sharing UI); returns null when the caller is not a
    // member so the endpoint can 404 without leaking existence.
    public async Task<IReadOnlyList<FullWorthSpaceMemberView>?> ListMembersForUserAsync(Guid actingUserId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        ValidateUserId(actingUserId, nameof(actingUserId));
        ValidateFullWorthSpaceId(fullWorthSpaceId);
        if (!await IsMemberAsync(actingUserId, fullWorthSpaceId, ct)) return null;

        // Project to an anonymous shape, order, and materialize BEFORE mapping to the DTO record — EF
        // cannot translate an OrderBy over a projected custom record produced by a Join.
        var rows = await db.Set<FullWorthSpaceMember>()
            .AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId)
            .Join(db.Set<FullWorthUser>(), member => member.UserId, user => user.Id,
                (member, user) => new { user.Id, user.DisplayName, user.EmailNormalized, member.Role, member.JoinedAt })
            .OrderBy(row => row.DisplayName)
            .ThenBy(row => row.Id)
            .ToListAsync(ct);
        return rows.Select(row => new FullWorthSpaceMemberView(row.Id, row.DisplayName, row.EmailNormalized, row.Role, row.JoinedAt)).ToList();
    }

    public Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        ValidateUserId(userId, nameof(userId));
        ValidateFullWorthSpaceId(fullWorthSpaceId);
        return db.Set<FullWorthSpaceMember>()
            .AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);
    }

    public Task<bool> IsOwnerAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Set<FullWorthSpaceMember>()
            .AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId
                && member.UserId == userId
                && member.Role == FullWorthSpaceRoles.Owner, ct);

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("FullWorth Space name is required.", nameof(name));
        }
        return name.Trim();
    }

    private static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return "EUR";
        }
        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || normalized.Any(character => character < 'A' || character > 'Z'))
        {
            throw new ArgumentException("Base currency must be a three-letter currency code.", nameof(currency));
        }
        return normalized;
    }

    private static void ValidateRole(string role)
    {
        if (!FullWorthSpaceRoles.IsValid(role))
        {
            throw new ArgumentException("Role must be owner or member.", nameof(role));
        }
    }

    private static void ValidateUserId(Guid userId, string parameterName)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID is required.", parameterName);
        }
    }

    private static void ValidateFullWorthSpaceId(Guid fullWorthSpaceId)
    {
        if (fullWorthSpaceId == Guid.Empty)
        {
            throw new ArgumentException("FullWorth Space ID is required.", nameof(fullWorthSpaceId));
        }
    }
}

public sealed class FullWorthSpaceNotFoundException : InvalidOperationException
{
    public FullWorthSpaceNotFoundException() : base("FullWorth Space was not found or is not accessible.") { }
}

public sealed class FullWorthSpaceMembershipExistsException : InvalidOperationException
{
    public FullWorthSpaceMembershipExistsException() : base("The user is already a member of this FullWorth Space.") { }
}

public sealed class FullWorthSpaceLastOwnerException : InvalidOperationException
{
    public FullWorthSpaceLastOwnerException() : base("The last FullWorth Space owner cannot be removed.") { }
}
