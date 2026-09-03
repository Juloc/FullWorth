namespace FullWorth.Web.Modules.Sessions;

public enum SessionValidationStatus
{
    Valid,
    NotFound,
    Revoked,
    IdleExpired,
    AbsoluteExpired,
    UserInvalid,
    SecurityStampChanged
}

public sealed record SessionUserSecurityState(bool IsActive, string? SecurityStamp);

public sealed record SessionValidationResult(SessionValidationStatus Status, bool LastSeenUpdated = false)
{
    public bool IsValid => Status == SessionValidationStatus.Valid;
}
