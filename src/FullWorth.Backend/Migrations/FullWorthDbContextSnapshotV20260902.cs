using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Frozen snapshot delta for the Tax Assistant model introduced on 2026-09-02.
/// Keep this string-based so later CLR model changes remain visible as pending model changes.
/// </summary>
internal static class FullWorthDbContextSnapshotDeltaV20260902
{
    internal static void Apply(ModelBuilder modelBuilder)
    {
        FullWorthDbContextSnapshotDeltaV20260830.Apply(modelBuilder);
        CoachSnapshot.Apply(modelBuilder);
        TaxAssistantSnapshotCore.Apply(modelBuilder);
        TaxAssistantSnapshotCandidates.Apply(modelBuilder);
        TaxAssistantSnapshotRelationships.Apply(modelBuilder);
    }
}
