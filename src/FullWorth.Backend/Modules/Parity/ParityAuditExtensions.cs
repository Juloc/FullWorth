using FullWorth.Backend.Modules.Audit;

namespace FullWorth.Backend.Modules.Parity;

internal static class ParityAuditExtensions
{
    /// <summary>
    /// Compatibility overload for parity workflows that calculate useful result metadata but must not
    /// persist arbitrary objects into the security audit log. AuditService intentionally has a narrow
    /// contract, so the safe metadata argument is discarded and only identifiers/action are recorded.
    /// </summary>
    public static void Record(
        this AuditService audit,
        Guid? fullWorthSpaceId,
        Guid? actorUserId,
        string action,
        string entityType,
        Guid? entityId,
        object? safeMetadata)
    {
        audit.Record(fullWorthSpaceId, actorUserId, action, entityType, entityId);
    }
}
