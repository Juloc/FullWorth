using System;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Snapshot delta for 20260911120000_PensionAllocationDocument: the document a fund position was read
/// from. The migration adds the column with raw SQL, so without this the EF model snapshot would lag
/// behind the entity and startup migration would trip PendingModelChangesWarning.
///
/// String-based like <see cref="OccupationalPensionSnapshot"/>, so a later change to
/// <c>BavInvestmentAllocation</c> still surfaces as a pending model change instead of being absorbed here.
/// </summary>
internal static class PensionAllocationDocumentSnapshot
{
    private const string Allocation = "FullWorth.Backend.Modules.Pension.BavInvestmentAllocation";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Allocation, b =>
        {
            b.Property<Guid?>("BavDocumentId").HasColumnType("uuid");
            b.HasIndex("BavDocumentId");
        });
    }
}
