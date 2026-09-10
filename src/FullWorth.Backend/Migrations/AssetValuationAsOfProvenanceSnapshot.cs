using System;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Snapshot delta for 20260911003000_AssetValuationAsOfProvenance: when the asset's current value was
/// recorded, as opposed to the date somebody stated it holds for. The migration adds the column with
/// raw SQL, so without this the EF model snapshot would lag behind and startup migration would trip
/// PendingModelChangesWarning. ("ValuedAtIsStated" needs no entry - "AssetValuations" is read and
/// written with raw SQL and has no CLR entity.)
/// </summary>
internal static class AssetValuationAsOfProvenanceSnapshot
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("FullWorth.Backend.Modules.Portfolio.Asset", b =>
        {
            b.Property<DateTimeOffset>("ValueRecordedAt").HasColumnType("timestamp with time zone");
        });
    }
}
