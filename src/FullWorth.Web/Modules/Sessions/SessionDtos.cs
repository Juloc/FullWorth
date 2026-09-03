namespace FullWorth.Web.Modules.Sessions;

public sealed record SessionDto(
    Guid Id,
    string DeviceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool Current,
    bool Active);

public sealed record SessionListDto(IReadOnlyList<SessionDto> Sessions);
