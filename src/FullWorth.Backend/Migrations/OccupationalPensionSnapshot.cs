using System;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Frozen snapshot delta for the bAV model introduced by 20260910233000_OccupationalPension. That
/// migration creates its six tables with idempotent raw SQL and therefore does not update the EF model
/// snapshot, so without this the entities would be missing from the baseline and startup migration
/// would trip PendingModelChangesWarning.
///
/// String-based on purpose (same as CoachSnapshot): a later change to the CLR entities then still shows
/// up as a pending model change instead of being silently absorbed here.
/// </summary>
internal static class OccupationalPensionSnapshot
{
    private const string Contract = "FullWorth.Backend.Modules.Pension.BavContract";
    private const string Snapshot = "FullWorth.Backend.Modules.Pension.BavSnapshot";
    private const string Contribution = "FullWorth.Backend.Modules.Pension.BavContribution";
    private const string Allocation = "FullWorth.Backend.Modules.Pension.BavInvestmentAllocation";
    private const string Cost = "FullWorth.Backend.Modules.Pension.BavCost";
    private const string Document = "FullWorth.Backend.Modules.Pension.BavDocument";

    private const string Space = "FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpace";
    private const string User = "FullWorth.Backend.Modules.Users.FullWorthUser";
    private const string Asset = "FullWorth.Backend.Modules.Portfolio.Asset";
    private const string RecurringContract = "FullWorth.Backend.Modules.Contracts.RecurringContract";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Contract, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid?>("AssetId").HasColumnType("uuid");
            b.Property<DateOnly?>("ContractEndDate").HasColumnType("date");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<string>("EmployerName").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<bool>("FundSelectionChangeable").HasColumnType("boolean");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<decimal?>("GuaranteeQuotaPercent").HasPrecision(9, 4).HasColumnType("numeric(9,4)");
            b.Property<decimal?>("GuaranteedAnnuityFactor").HasPrecision(12, 4).HasColumnType("numeric(12,4)");
            b.Property<string>("ImplementationRoute").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("InsuredPersonName").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("Notes").HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<string>("PolicyHolderName").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("PolicyNumberEncrypted").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("PolicyNumberLast4").HasMaxLength(8).HasColumnType("character varying(8)");
            b.Property<string>("PolicyNumberLookup").HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("ProviderKey").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("ProviderName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<Guid?>("RecurringContractId").HasColumnType("uuid");
            b.Property<DateOnly?>("RetirementDate").HasColumnType("date");
            b.Property<DateOnly?>("StartDate").HasColumnType("date");
            b.Property<string>("Status").IsRequired().HasMaxLength(24).HasColumnType("character varying(24)");
            b.Property<string>("TariffName").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("AssetId");
            b.HasIndex("CreatedByUserId");
            b.HasIndex("FullWorthSpaceId");
            b.HasIndex("RecurringContractId");
            b.HasIndex("FullWorthSpaceId", "ProviderKey");
            b.HasIndex("FullWorthSpaceId", "ProviderKey", "PolicyNumberLookup")
                .IsUnique()
                .HasDatabaseName("UX_BavContracts_PolicyIdentity")
                .HasFilter("\"PolicyNumberLookup\" IS NOT NULL");
            b.ToTable("BavContracts", (string)null);
        });

        modelBuilder.Entity(Document, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid?>("BavContractId").HasColumnType("uuid");
            b.Property<long>("ByteSize").HasColumnType("bigint");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<string>("EncryptionScheme").HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<decimal?>("ExtractionConfidence").HasPrecision(5, 4).HasColumnType("numeric(5,4)");
            b.Property<string>("ExtractionSource").HasMaxLength(24).HasColumnType("character varying(24)");
            b.Property<string>("ExtractionStatus").IsRequired().HasMaxLength(24).HasColumnType("character varying(24)");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<string>("Kind").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("MediaType").HasMaxLength(150).HasColumnType("character varying(150)");
            b.Property<string>("OriginalFileName").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<int?>("PageCount").HasColumnType("integer");
            b.Property<DateTimeOffset?>("ReviewedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("ReviewedByUserId").HasColumnType("uuid");
            b.Property<string>("Sha256").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("StoragePath").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.HasKey("Id");
            b.HasIndex("BavContractId");
            b.HasIndex("CreatedByUserId");
            b.HasIndex("ReviewedByUserId");
            b.HasIndex("FullWorthSpaceId", "Sha256").IsUnique();
            b.ToTable("BavDocuments", (string)null);
        });

        modelBuilder.Entity(Snapshot, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<decimal?>("Balance").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<Guid>("BavContractId").HasColumnType("uuid");
            b.Property<Guid?>("BavDocumentId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<string>("DocumentSha256").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<DateOnly>("EffectiveDate").HasColumnType("date");
            b.Property<decimal?>("ExtractionConfidence").HasPrecision(5, 4).HasColumnType("numeric(5,4)");
            b.Property<decimal?>("FundAssetsAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<decimal?>("GuaranteedBalance").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("GuaranteedCapitalAtRetirement").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("GuaranteedMonthlyAnnuity").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<bool>("IsCurrent").HasColumnType("boolean");
            b.Property<string>("Note").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<decimal?>("ProjectedCapitalAtRetirement").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("ProjectedMonthlyAnnuity").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("ProjectionBasis").HasMaxLength(24).HasColumnType("character varying(24)");
            b.Property<decimal?>("ProjectionReturnPercent").HasPrecision(9, 4).HasColumnType("numeric(9,4)");
            b.Property<decimal?>("SecurityAssetsAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("Source").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<decimal?>("SurrenderValue").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.HasKey("Id");
            b.HasIndex("BavDocumentId");
            b.HasIndex("CreatedByUserId");
            b.HasIndex("FullWorthSpaceId");
            b.HasIndex("BavContractId", "EffectiveDate").IsDescending(false, true);
            b.HasIndex("BavContractId", "EffectiveDate", "DocumentSha256")
                .IsUnique()
                .HasDatabaseName("UX_BavSnapshots_Identity");
            b.HasIndex("BavContractId")
                .IsUnique()
                .HasDatabaseName("UX_BavSnapshots_Current")
                .HasFilter("\"IsCurrent\"");
            b.ToTable("BavSnapshots", (string)null);
        });

        modelBuilder.Entity(Contribution, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("BavContractId").HasColumnType("uuid");
            b.Property<Guid?>("BavDocumentId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<string>("Cycle").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<decimal>("EmployeeAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal>("EmployerAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal>("EmployerSubsidyAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("EndReason").HasMaxLength(24).HasColumnType("character varying(24)");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<decimal?>("NetEffortAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("Note").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<decimal?>("SocialSecuritySavingAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("Source").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<decimal?>("StatedTotalAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("TaxSavingAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("TaxEffectSource").HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<string>("TaxEffectSourceReference").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<DateOnly>("ValidFrom").HasColumnType("date");
            b.Property<DateOnly?>("ValidUntil").HasColumnType("date");
            b.HasKey("Id");
            b.HasIndex("BavDocumentId");
            b.HasIndex("CreatedByUserId");
            b.HasIndex("FullWorthSpaceId");
            b.HasIndex("BavContractId", "ValidFrom").IsDescending(false, true);
            b.ToTable("BavContributions", (string)null);
        });

        modelBuilder.Entity(Allocation, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<decimal?>("Amount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("AssetClass").IsRequired().HasMaxLength(24).HasColumnType("character varying(24)");
            b.Property<Guid>("BavContractId").HasColumnType("uuid");
            b.Property<Guid?>("BavSnapshotId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<DateOnly>("EffectiveDate").HasColumnType("date");
            b.Property<string>("FundName").IsRequired().HasMaxLength(300).HasColumnType("character varying(300)");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<string>("Isin").HasMaxLength(12).HasColumnType("character varying(12)");
            b.Property<string>("Note").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<bool>("OngoingChargesEstimated").HasColumnType("boolean");
            b.Property<decimal?>("OngoingChargesPercent").HasPrecision(9, 4).HasColumnType("numeric(9,4)");
            b.Property<string>("Source").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<decimal?>("WeightPercent").HasPrecision(9, 4).HasColumnType("numeric(9,4)");
            b.HasKey("Id");
            b.HasIndex("BavSnapshotId");
            b.HasIndex("CreatedByUserId");
            b.HasIndex("FullWorthSpaceId");
            b.HasIndex("BavContractId", "EffectiveDate").IsDescending(false, true);
            b.ToTable("BavInvestmentAllocations", (string)null);
        });

        modelBuilder.Entity(Cost, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<decimal?>("Amount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<DateOnly?>("AppliesUntilDate").HasColumnType("date");
            b.Property<string>("Basis").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<Guid>("BavContractId").HasColumnType("uuid");
            b.Property<Guid?>("BavDocumentId").HasColumnType("uuid");
            b.Property<Guid?>("BavSnapshotId").HasColumnType("uuid");
            b.Property<bool>("ContinuesWhenPaidUp").HasColumnType("boolean");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<DateOnly>("EffectiveDate").HasColumnType("date");
            b.Property<string>("EstimateBasis").HasMaxLength(300).HasColumnType("character varying(300)");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<bool>("IsEstimated").HasColumnType("boolean");
            b.Property<string>("Kind").IsRequired().HasMaxLength(40).HasColumnType("character varying(40)");
            b.Property<string>("Note").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<decimal?>("Percent").HasPrecision(9, 4).HasColumnType("numeric(9,4)");
            b.Property<string>("Source").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<string>("Timing").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.HasKey("Id");
            b.HasIndex("BavDocumentId");
            b.HasIndex("BavSnapshotId");
            b.HasIndex("CreatedByUserId");
            b.HasIndex("FullWorthSpaceId");
            b.HasIndex("BavContractId", "EffectiveDate").IsDescending(false, true);
            b.ToTable("BavCosts", (string)null);
        });

        modelBuilder.Entity(Contract, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Asset, null).WithMany().HasForeignKey("AssetId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(RecurringContract, null).WithMany().HasForeignKey("RecurringContractId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("CreatedByUserId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Document, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Contract, null).WithMany().HasForeignKey("BavContractId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("ReviewedByUserId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("CreatedByUserId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Snapshot, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Contract, null).WithMany().HasForeignKey("BavContractId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Document, null).WithMany().HasForeignKey("BavDocumentId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("CreatedByUserId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Contribution, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Contract, null).WithMany().HasForeignKey("BavContractId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Document, null).WithMany().HasForeignKey("BavDocumentId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("CreatedByUserId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Allocation, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Contract, null).WithMany().HasForeignKey("BavContractId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Snapshot, null).WithMany().HasForeignKey("BavSnapshotId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("CreatedByUserId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(Cost, b =>
        {
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Contract, null).WithMany().HasForeignKey("BavContractId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Snapshot, null).WithMany().HasForeignKey("BavSnapshotId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(Document, null).WithMany().HasForeignKey("BavDocumentId").OnDelete(DeleteBehavior.SetNull);
            b.HasOne(User, null).WithMany().HasForeignKey("CreatedByUserId").OnDelete(DeleteBehavior.SetNull);
        });
    }
}
