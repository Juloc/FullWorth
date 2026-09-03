namespace FullWorth.Web.Security.BackendContext;

public static class BackendContextHeaders
{
    public const string InternalKey = "X-FullWorth-Internal-Key";
    public const string UserId = "X-FullWorth-User-Id";
    public const string SpaceId = "X-FullWorth-Space-Id";
    public const string LegacyApiKey = "X-FullWorth-Key";
    public const string LegacyReadKey = "X-FullWorth-Read-Key";
    public const string IngestKey = "X-FullWorth-Ingest-Key";

    public static readonly string[] UntrustedForwardingHeaders =
    [
        InternalKey,
        UserId,
        SpaceId,
        LegacyApiKey,
        LegacyReadKey,
        IngestKey,
        "Authorization",
        "Cookie"
    ];
}
