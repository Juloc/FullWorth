namespace FullWorth.Web.Modules.Sessions;

public sealed class UserSession
{
    public const int MaxDeviceNameLength = 120;
    public const int MaxUserAgentLength = 512;
    public const int MaxIpAddressLength = 64;
    public const int MaxSecurityStampLength = 256;

    public Guid Id { get; set; }
    public Guid AuthUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string DeviceName { get; set; } = "Browser session";
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
    public string? SecurityStampAtIssue { get; set; }
}
