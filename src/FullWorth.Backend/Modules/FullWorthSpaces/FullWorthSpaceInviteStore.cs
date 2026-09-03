using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public enum InviteCreateStatus { Ok, SpaceNotFound, NotOwner, InvalidEmail, AlreadyMember, OpenInviteExists, InvalidGrant }
public enum InviteAdminStatus { Ok, SpaceNotFound, NotOwner, InviteNotFound, AlreadyClaimed }

public sealed record InviteView(Guid Id, string Email, string SpaceRole, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int AccountCount);
public sealed record InviteCreateResult(InviteCreateStatus Status, InviteView? View = null, string? ClaimToken = null);
public sealed record InviteAcceptResult(bool Ok, Guid FinanceUserId = default, string Email = "");

/// <summary>
/// Owner-issued FullWorth Space invitations (multi-user sharing). Creation and management are gated to
/// space owners; acceptance runs with no acting user (the internal-key bootstrap seam) and creates or
/// reuses the invitee's FullWorthUser, their membership, and the requested AccountOwner grants in one
/// transaction. Tokens are stored only as a SHA-256 hash; the raw token is surfaced once at creation.
/// </summary>
public sealed class FullWorthSpaceInviteStore(FullWorthDbContext db, FullWorthSpaceStore spaces, UserStore users, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);

    public async Task<InviteCreateResult> CreateAsync(Guid actingUserId, Guid fullWorthSpaceId, string? email, string? spaceRole, IReadOnlyList<InviteAccountGrant> grants, CancellationToken ct)
    {
        if (!await spaces.IsMemberAsync(actingUserId, fullWorthSpaceId, ct)) return new(InviteCreateStatus.SpaceNotFound);
        if (!await spaces.IsOwnerAsync(actingUserId, fullWorthSpaceId, ct)) return new(InviteCreateStatus.NotOwner);

        var normalizedEmail = NormalizeEmail(email);
        if (normalizedEmail is null) return new(InviteCreateStatus.InvalidEmail);
        var role = FullWorthSpaceRoles.IsValid(spaceRole) ? spaceRole! : FullWorthSpaceRoles.Member;

        // Already a member? (resolve the email to a user, then check membership in THIS space.)
        var existing = await users.GetByEmailAsync(normalizedEmail, ct);
        if (existing is not null && await spaces.IsMemberAsync(existing.Id, fullWorthSpaceId, ct))
            return new(InviteCreateStatus.AlreadyMember);

        var now = DateTimeOffset.UtcNow;
        if (await db.FullWorthSpaceInvites.AnyAsync(x => x.FullWorthSpaceId == fullWorthSpaceId
                && x.EmailNormalized == normalizedEmail && x.Status == FullWorthSpaceInviteStatuses.Pending && x.ExpiresAt > now, ct))
            return new(InviteCreateStatus.OpenInviteExists);

        // Only accounts the acting user OWNS, in THIS space, may be shared — validate every grant.
        foreach (var grant in grants)
        {
            if (!AccountOwnershipTypes.IsValid(grant.OwnershipType)) return new(InviteCreateStatus.InvalidGrant);
            var accountInSpace = await db.Accounts.AnyAsync(a => a.Id == grant.AccountId && a.FullWorthSpaceId == fullWorthSpaceId, ct);
            if (!accountInSpace) return new(InviteCreateStatus.InvalidGrant);
            var ownsAccount = await db.AccountOwners.AnyAsync(o => o.AccountId == grant.AccountId
                    && o.UserId == actingUserId && o.OwnershipType == AccountOwnershipTypes.Owner, ct);
            if (!ownsAccount) return new(InviteCreateStatus.InvalidGrant);
        }

        var rawToken = GenerateToken();
        var invite = new FullWorthSpaceInvite
        {
            FullWorthSpaceId = fullWorthSpaceId,
            EmailNormalized = normalizedEmail,
            SpaceRole = role,
            TokenHash = HashToken(rawToken),
            AccountGrantsJson = JsonSerializer.Serialize(grants),
            Status = FullWorthSpaceInviteStatuses.Pending,
            InvitedByUserId = actingUserId,
            CreatedAt = now,
            ExpiresAt = now + Lifetime
        };
        db.FullWorthSpaceInvites.Add(invite);
        audit.Record(fullWorthSpaceId, actingUserId, "space.invite.created", "FullWorthSpaceInvite", invite.Id);
        await db.SaveChangesAsync(ct);
        return new(InviteCreateStatus.Ok, ToView(invite, grants.Count), rawToken);
    }

    public async Task<(InviteAdminStatus Status, IReadOnlyList<InviteView> Invites)> ListAsync(Guid actingUserId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await spaces.IsMemberAsync(actingUserId, fullWorthSpaceId, ct)) return (InviteAdminStatus.SpaceNotFound, []);
        if (!await spaces.IsOwnerAsync(actingUserId, fullWorthSpaceId, ct)) return (InviteAdminStatus.NotOwner, []);

        var rows = await db.FullWorthSpaceInvites.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.Status == FullWorthSpaceInviteStatuses.Pending)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.EmailNormalized, x.SpaceRole, x.Status, x.CreatedAt, x.ExpiresAt, x.AccountGrantsJson })
            .ToListAsync(ct);
        var views = rows.Select(x => new InviteView(x.Id, x.EmailNormalized, x.SpaceRole, x.Status, x.CreatedAt, x.ExpiresAt, CountGrants(x.AccountGrantsJson))).ToList();
        return (InviteAdminStatus.Ok, views);
    }

    public async Task<InviteAdminStatus> RevokeAsync(Guid actingUserId, Guid fullWorthSpaceId, Guid inviteId, CancellationToken ct)
    {
        if (!await spaces.IsMemberAsync(actingUserId, fullWorthSpaceId, ct)) return InviteAdminStatus.SpaceNotFound;
        if (!await spaces.IsOwnerAsync(actingUserId, fullWorthSpaceId, ct)) return InviteAdminStatus.NotOwner;

        var invite = await db.FullWorthSpaceInvites.SingleOrDefaultAsync(x => x.Id == inviteId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (invite is null) return InviteAdminStatus.InviteNotFound;
        if (invite.Status != FullWorthSpaceInviteStatuses.Pending) return InviteAdminStatus.AlreadyClaimed;

        invite.Status = FullWorthSpaceInviteStatuses.Revoked;
        audit.Record(fullWorthSpaceId, actingUserId, "space.invite.revoked", "FullWorthSpaceInvite", invite.Id);
        await db.SaveChangesAsync(ct);
        return InviteAdminStatus.Ok;
    }

    /// <summary>Claims a pending, unexpired invite (internal seam; no acting user). Creates/reuses the
    /// invitee's FullWorthUser, their membership, and the requested account grants in one transaction.</summary>
    public async Task<InviteAcceptResult> AcceptAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken)) return new(false);
        var hash = HashToken(rawToken);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invite = await db.FullWorthSpaceInvites.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (invite is null || invite.Status != FullWorthSpaceInviteStatuses.Pending || invite.ExpiresAt <= now)
            return new(false);

        // Create or reuse the invitee's global identity (idempotent by normalized email).
        var user = await db.Users.SingleOrDefaultAsync(x => x.EmailNormalized == invite.EmailNormalized, ct);
        if (user is null)
        {
            user = new FullWorthUser
            {
                EmailNormalized = invite.EmailNormalized,
                DisplayName = DeriveDisplayName(invite.EmailNormalized),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Users.Add(user);
        }

        // Membership (idempotent).
        if (!await db.FullWorthSpaceMembers.AnyAsync(m => m.FullWorthSpaceId == invite.FullWorthSpaceId && m.UserId == user.Id, ct))
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = invite.FullWorthSpaceId, UserId = user.Id, Role = invite.SpaceRole, JoinedAt = now });

        // Apply the requested grants — only for accounts still in the space; upsert the ownership row.
        var grants = ParseGrants(invite.AccountGrantsJson);
        foreach (var grant in grants)
        {
            if (!AccountOwnershipTypes.IsValid(grant.OwnershipType)) continue;
            if (!await db.Accounts.AnyAsync(a => a.Id == grant.AccountId && a.FullWorthSpaceId == invite.FullWorthSpaceId, ct)) continue;
            var owner = await db.AccountOwners.SingleOrDefaultAsync(o => o.AccountId == grant.AccountId && o.UserId == user.Id, ct);
            if (owner is null)
                db.AccountOwners.Add(new AccountOwner { AccountId = grant.AccountId, UserId = user.Id, OwnershipType = grant.OwnershipType, CreatedAt = now });
            else
                owner.OwnershipType = grant.OwnershipType;
        }

        invite.Status = FullWorthSpaceInviteStatuses.Claimed;
        invite.ClaimedAt = now;
        audit.Record(invite.FullWorthSpaceId, user.Id, "space.invite.claimed", "FullWorthSpaceInvite", invite.Id);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent claim of the same token (or a racing user creation) lost the unique-constraint
            // race; the transaction rolls back. Surface a clean "invalid" rather than a 500.
            return new(false);
        }
        return new(true, user.Id, invite.EmailNormalized);
    }

    private static InviteView ToView(FullWorthSpaceInvite invite, int accountCount) =>
        new(invite.Id, invite.EmailNormalized, invite.SpaceRole, invite.Status, invite.CreatedAt, invite.ExpiresAt, accountCount);

    private static int CountGrants(string json) => ParseGrants(json).Count;

    private static List<InviteAccountGrant> ParseGrants(string json)
    {
        try { return JsonSerializer.Deserialize<List<InviteAccountGrant>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToUpperInvariant();
        if (normalized.Length > 320 || !normalized.Contains('@')) return null;
        return normalized;
    }

    private static string DeriveDisplayName(string normalizedEmail)
    {
        var at = normalizedEmail.IndexOf('@');
        var local = at > 0 ? normalizedEmail[..at] : normalizedEmail;
        return string.IsNullOrWhiteSpace(local) ? normalizedEmail : local;
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string HashToken(string rawToken)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
