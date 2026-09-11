using System;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Snapshot delta for 20260911010000_PensionDocumentExtraction: the four columns that let a pension
/// document sit between extraction and review. The migration adds them with raw SQL, so without this
/// the EF model snapshot would lag behind the entity and startup migration would trip
/// PendingModelChangesWarning.
///
/// String-based like <see cref="OccupationalPensionSnapshot"/>, so a later change to
/// <c>BavDocument</c> still surfaces as a pending model change instead of being absorbed here.
/// </summary>
internal static class PensionDocumentExtractionSnapshot
{
    private const string Document = "FullWorth.Backend.Modules.Pension.BavDocument";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Document, b =>
        {
            b.Property<string>("ExtractionDraftJson").HasColumnType("text");
            b.Property<string>("ExtractionError").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<DateTimeOffset?>("ExtractedAt").HasColumnType("timestamp with time zone");
            b.Property<bool?>("TextLayerUsed").HasColumnType("boolean");
        });
    }
}
