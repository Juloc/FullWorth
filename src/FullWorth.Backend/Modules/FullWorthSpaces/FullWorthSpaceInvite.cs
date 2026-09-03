namespace FullWorth.Backend.Modules.FullWorthSpaces;

/// <summary>
/// An owner-issued invitation to join a FullWorth Space (UI_UX_SPEC multi-user sharing). The owner creates
/// the invite, hands the one-time claim token to the invitee out-of-band, and the invitee sets their own
/// password to claim it — so the backend never sees a password and the owner never types another person's
/// credential. Only a SHA-256 hash of the token is stored; the raw token is returned once at creation.
/// The requested account grants (which accounts to share, and at what level) are held as plain JSON and
/// applied at claim time; they are never queried in LINQ, avoiding translation surprises.
/// </summary>
public sealed class FullWorthSpaceInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string EmailNormalized { get; set; } = string.Empty;
    public string SpaceRole { get; set; } = FullWorthSpaceRoles.Member;
    public string TokenHash { get; set; } = string.Empty;
    public string AccountGrantsJson { get; set; } = "[]";
    public string Status { get; set; } = FullWorthSpaceInviteStatuses.Pending;
    public Guid? InvitedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
}

public static class FullWorthSpaceInviteStatuses
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Revoked = "revoked";
}

/// <summary>One requested account grant carried by an invite; applied as an AccountOwner row at claim.</summary>
public sealed record InviteAccountGrant(Guid AccountId, string OwnershipType);
