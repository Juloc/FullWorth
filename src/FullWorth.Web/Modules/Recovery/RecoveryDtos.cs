namespace FullWorth.Web.Modules.Recovery;

public sealed record RecoveryCodeSetDto(
    IReadOnlyList<string> Codes,
    DateTimeOffset GeneratedAt);

public sealed record RecoveryCodeStatusDto(
    int RemainingCount,
    DateTimeOffset? GeneratedAt);

public sealed record UseRecoveryCodeRequest(string Code);
