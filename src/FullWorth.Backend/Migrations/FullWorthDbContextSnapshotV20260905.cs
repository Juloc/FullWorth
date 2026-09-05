using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Frozen snapshot delta chain as of 2026-09-05: the prior 2026-09-02 chain plus the §4 merchant brand
/// visuals. Wrapping the previous delta mirrors the established baseline+delta pattern so each new schema
/// change layers on the last frozen model.
/// </summary>
internal static class FullWorthDbContextSnapshotDeltaV20260905
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        FullWorthDbContextSnapshotDeltaV20260902.Apply(modelBuilder);
        MerchantBrandSnapshot.Apply(modelBuilder);
    }
}
