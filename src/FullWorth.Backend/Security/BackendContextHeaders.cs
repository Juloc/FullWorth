namespace FullWorth.Backend.Security;

public static class BackendContextHeaders
{
    public const string InternalKey = "X-FullWorth-Internal-Key";
    public const string UserId = "X-FullWorth-User-Id";
    public const string IngestKey = "X-FullWorth-Ingest-Key";
    public const string LegacyApiKey = "X-FullWorth-Key";
    public const string LegacyReadKey = "X-FullWorth-Read-Key";
}
