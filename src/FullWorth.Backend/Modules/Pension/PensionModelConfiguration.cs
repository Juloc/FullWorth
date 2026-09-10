using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// EF mapping for the bAV aggregate. Kept next to the entities like <c>TaxModelConfiguration</c>, and
/// mirrored one-to-one by <c>Migrations/OccupationalPensionSnapshot.cs</c> — the tables are created by
/// a raw-SQL migration, so the model snapshot delta is what keeps EF from reporting pending changes.
///
/// Money columns are numeric(20,8) and currency columns are three characters, the same as everywhere
/// else, so a pension value can be summed with the rest of the portfolio without a conversion step.
/// </summary>
public static class PensionModelConfiguration
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<BavContract>(e =>
        {
            e.ToTable("BavContracts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.HasIndex(x => new { x.FullWorthSpaceId, x.ProviderKey });
            e.HasIndex(x => x.AssetId);
            e.HasIndex(x => x.RecurringContractId);
            // The identity of a contract: the same policy number at the same provider is the same
            // contract. Partial, because a hand-entered contract may have no number at all and two of
            // those are not automatically the same thing.
            e.HasIndex(x => new { x.FullWorthSpaceId, x.ProviderKey, x.PolicyNumberLookup })
                .IsUnique()
                .HasDatabaseName("UX_BavContracts_PolicyIdentity")
                .HasFilter("\"PolicyNumberLookup\" IS NOT NULL");
            e.Property(x => x.ProviderName).IsRequired().HasMaxLength(200);
            e.Property(x => x.ProviderKey).IsRequired().HasMaxLength(200);
            e.Property(x => x.TariffName).HasMaxLength(200);
            e.Property(x => x.PolicyNumberEncrypted).HasMaxLength(500);
            e.Property(x => x.PolicyNumberLookup).HasMaxLength(128);
            e.Property(x => x.PolicyNumberLast4).HasMaxLength(8);
            e.Property(x => x.ImplementationRoute).IsRequired().HasMaxLength(32);
            e.Property(x => x.Status).IsRequired().HasMaxLength(24);
            e.Property(x => x.EmployerName).HasMaxLength(200);
            e.Property(x => x.PolicyHolderName).HasMaxLength(200);
            e.Property(x => x.InsuredPersonName).HasMaxLength(200);
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            e.Property(x => x.GuaranteeQuotaPercent).HasPrecision(9, 4);
            e.Property(x => x.GuaranteedAnnuityFactor).HasPrecision(12, 4);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<RecurringContract>().WithMany().HasForeignKey(x => x.RecurringContractId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BavDocument>(e =>
        {
            e.ToTable("BavDocuments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.BavContractId);
            e.HasIndex(x => new { x.FullWorthSpaceId, x.Sha256 }).IsUnique();
            e.Property(x => x.Sha256).IsRequired().HasMaxLength(64);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(32);
            e.Property(x => x.OriginalFileName).HasMaxLength(500);
            e.Property(x => x.MediaType).HasMaxLength(150);
            e.Property(x => x.StoragePath).HasMaxLength(1000);
            e.Property(x => x.EncryptionScheme).HasMaxLength(32);
            e.Property(x => x.ExtractionStatus).IsRequired().HasMaxLength(24);
            e.Property(x => x.ExtractionConfidence).HasPrecision(5, 4);
            e.Property(x => x.ExtractionSource).HasMaxLength(24);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BavContract>().WithMany().HasForeignKey(x => x.BavContractId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BavSnapshot>(e =>
        {
            e.ToTable("BavSnapshots");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.HasIndex(x => x.BavDocumentId);
            e.HasIndex(x => new { x.BavContractId, x.EffectiveDate }).IsDescending(false, true);
            // A snapshot is identified by contract + date + document. The migration creates this index
            // with NULLS NOT DISTINCT so a second hand-entered snapshot for the same date is a conflict
            // rather than a silent duplicate — history is added to, never rewritten.
            e.HasIndex(x => new { x.BavContractId, x.EffectiveDate, x.DocumentSha256 })
                .IsUnique()
                .HasDatabaseName("UX_BavSnapshots_Identity");
            e.HasIndex(x => x.BavContractId)
                .IsUnique()
                .HasDatabaseName("UX_BavSnapshots_Current")
                .HasFilter("\"IsCurrent\"");
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            e.Property(x => x.Balance).HasPrecision(20, 8);
            e.Property(x => x.GuaranteedBalance).HasPrecision(20, 8);
            e.Property(x => x.SurrenderValue).HasPrecision(20, 8);
            e.Property(x => x.SecurityAssetsAmount).HasPrecision(20, 8);
            e.Property(x => x.FundAssetsAmount).HasPrecision(20, 8);
            e.Property(x => x.GuaranteedCapitalAtRetirement).HasPrecision(20, 8);
            e.Property(x => x.GuaranteedMonthlyAnnuity).HasPrecision(20, 8);
            e.Property(x => x.ProjectedCapitalAtRetirement).HasPrecision(20, 8);
            e.Property(x => x.ProjectedMonthlyAnnuity).HasPrecision(20, 8);
            e.Property(x => x.ProjectionReturnPercent).HasPrecision(9, 4);
            e.Property(x => x.ProjectionBasis).HasMaxLength(24);
            e.Property(x => x.Source).IsRequired().HasMaxLength(16);
            e.Property(x => x.DocumentSha256).HasMaxLength(64);
            e.Property(x => x.ExtractionConfidence).HasPrecision(5, 4);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BavContract>().WithMany().HasForeignKey(x => x.BavContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BavDocument>().WithMany().HasForeignKey(x => x.BavDocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BavContribution>(e =>
        {
            e.ToTable("BavContributions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.HasIndex(x => x.BavDocumentId);
            e.HasIndex(x => new { x.BavContractId, x.ValidFrom }).IsDescending(false, true);
            e.Property(x => x.EndReason).HasMaxLength(24);
            e.Property(x => x.Cycle).IsRequired().HasMaxLength(16);
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            e.Property(x => x.EmployeeAmount).HasPrecision(20, 8);
            e.Property(x => x.EmployerSubsidyAmount).HasPrecision(20, 8);
            e.Property(x => x.EmployerAmount).HasPrecision(20, 8);
            e.Property(x => x.StatedTotalAmount).HasPrecision(20, 8);
            e.Property(x => x.Source).IsRequired().HasMaxLength(16);
            e.Property(x => x.TaxSavingAmount).HasPrecision(20, 8);
            e.Property(x => x.SocialSecuritySavingAmount).HasPrecision(20, 8);
            e.Property(x => x.NetEffortAmount).HasPrecision(20, 8);
            e.Property(x => x.TaxEffectSource).HasMaxLength(16);
            e.Property(x => x.TaxEffectSourceReference).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BavContract>().WithMany().HasForeignKey(x => x.BavContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BavDocument>().WithMany().HasForeignKey(x => x.BavDocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BavInvestmentAllocation>(e =>
        {
            e.ToTable("BavInvestmentAllocations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.HasIndex(x => x.BavSnapshotId);
            e.HasIndex(x => new { x.BavContractId, x.EffectiveDate }).IsDescending(false, true);
            e.Property(x => x.FundName).IsRequired().HasMaxLength(300);
            e.Property(x => x.Isin).HasMaxLength(12);
            e.Property(x => x.WeightPercent).HasPrecision(9, 4);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            e.Property(x => x.OngoingChargesPercent).HasPrecision(9, 4);
            e.Property(x => x.AssetClass).IsRequired().HasMaxLength(24);
            e.Property(x => x.Source).IsRequired().HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BavContract>().WithMany().HasForeignKey(x => x.BavContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BavSnapshot>().WithMany().HasForeignKey(x => x.BavSnapshotId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BavCost>(e =>
        {
            e.ToTable("BavCosts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.HasIndex(x => x.BavSnapshotId);
            e.HasIndex(x => x.BavDocumentId);
            e.HasIndex(x => new { x.BavContractId, x.EffectiveDate }).IsDescending(false, true);
            e.Property(x => x.Kind).IsRequired().HasMaxLength(40);
            e.Property(x => x.Basis).IsRequired().HasMaxLength(32);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3);
            e.Property(x => x.Percent).HasPrecision(9, 4);
            e.Property(x => x.Timing).IsRequired().HasMaxLength(16);
            e.Property(x => x.EstimateBasis).HasMaxLength(300);
            e.Property(x => x.Source).IsRequired().HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BavContract>().WithMany().HasForeignKey(x => x.BavContractId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<BavSnapshot>().WithMany().HasForeignKey(x => x.BavSnapshotId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<BavDocument>().WithMany().HasForeignKey(x => x.BavDocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
