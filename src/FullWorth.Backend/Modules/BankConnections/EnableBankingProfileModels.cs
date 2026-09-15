using System.Text.Json;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// One Bring-Your-Own Enable Banking application per FullWorth user. The RSA private key and optional
/// Control Panel refresh token are encrypted with FieldCipher and are only returned through the
/// ingest-key-protected internal banking API.
/// </summary>

public sealed class EnableBankingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string PrivateKeyPem { get; set; } = string.Empty;
    public string? ControlPanelRefreshToken { get; set; }
    public string KeyFingerprint { get; set; } = string.Empty;
    public string Environment { get; set; } = "SANDBOX";
    public string ApplicationName { get; set; } = string.Empty;
    public bool Active { get; set; }
    public string ServicesJson { get; set; } = "[]";
    public string RedirectUrlsJson { get; set; } = "[]";
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
