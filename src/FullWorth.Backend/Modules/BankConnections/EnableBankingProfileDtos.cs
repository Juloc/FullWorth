using System.Text.Json;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// One Bring-Your-Own Enable Banking application per FullWorth user. The RSA private key and optional
/// Control Panel refresh token are encrypted with FieldCipher and are only returned through the
/// ingest-key-protected internal banking API.
/// </summary>

public sealed record EnableBankingProfileInternalDto(
    Guid Id,
    Guid UserId,
    string ApplicationId,
    string PrivateKeyPem,
    string KeyFingerprint,
    string Environment,
    string ApplicationName,
    bool Active,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RedirectUrls,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset UpdatedAt,
    string? ControlPanelRefreshToken = null);

public sealed record EnableBankingProfileWrite(
    Guid UserId,
    string ApplicationId,
    string PrivateKeyPem,
    string KeyFingerprint,
    string Environment,
    string ApplicationName,
    bool Active,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> RedirectUrls,
    DateTimeOffset VerifiedAt,
    string? ControlPanelRefreshToken = null);

public enum EnableBankingProfileDeleteResult { Deleted, NotFound, InUse }
