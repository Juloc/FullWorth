namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed record FullWorthSpaceDto(
    Guid Id,
    string Name,
    string BaseCurrency,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FullWorthSpaceMemberDto(
    Guid FullWorthSpaceId,
    Guid UserId,
    string Role,
    DateTimeOffset JoinedAt);

public sealed record CreateFullWorthSpaceRequest(string Name, string? BaseCurrency = null);

public sealed record AddFullWorthSpaceMemberRequest(Guid UserId, string Role);

// Safe co-member view for the sharing UI: display name + email of fellow members is permitted; no other
// FullWorthUser fields (IsActive, timestamps) are exposed.
public sealed record FullWorthSpaceMemberView(Guid UserId, string DisplayName, string Email, string Role, DateTimeOffset JoinedAt);

public sealed record AddMemberByEmailRequest(string Email, string Role);

// Invite creation: which email to invite, the space role, and which accounts to share (and at what level).
public sealed record CreateInviteRequest(string Email, string? Role, List<InviteAccountGrant>? Accounts);
public sealed record CreateInviteResponse(Guid InviteId, string Email, string SpaceRole, DateTimeOffset ExpiresAt, string ClaimToken);

// Internal claim seam (Web tier → backend): exchange a raw token for the resolved FullWorthUser.
public sealed record AcceptInviteRequest(string Token);
public sealed record AcceptInviteResponse(Guid FinanceUserId, string Email);
