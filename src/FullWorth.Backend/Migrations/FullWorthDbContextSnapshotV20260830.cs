using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Frozen, string-based EF model delta applied to the generated AddNotificationDedups target model.
/// Never reference current entity CLR types here: doing so would let future conventions/data annotations
/// mutate this historical snapshot and defeat pending-model-change detection.
/// </summary>
internal static class FullWorthDbContextSnapshotDeltaV20260830
{
    private const string Purchase = "FullWorth.Backend.Modules.Purchases.Purchase";
    private const string PurchaseItem = "FullWorth.Backend.Modules.Purchases.PurchaseItem";
    private const string Product = "FullWorth.Backend.Modules.Purchases.Product";
    private const string ProductAlias = "FullWorth.Backend.Modules.Purchases.ProductAlias";
    private const string ProductBarcode = "FullWorth.Backend.Modules.Purchases.ProductBarcode";
    private const string PaymentLink = "FullWorth.Backend.Modules.Purchases.PurchasePaymentLink";
    private const string PurchaseDiscount = "FullWorth.Backend.Modules.Purchases.PurchaseDiscount";
    private const string PurchaseAllocationLink = "FullWorth.Backend.Modules.Purchases.PurchaseAllocationLink";
    private const string Document = "FullWorth.Backend.Modules.Purchases.PurchaseDocument";
    private const string ExtractionRun = "FullWorth.Backend.Modules.Purchases.PurchaseExtractionRun";
    private const string DifferenceAcceptance = "FullWorth.Backend.Modules.Purchases.PurchaseDifferenceAcceptance";
    private const string ItemReturn = "FullWorth.Backend.Modules.Purchases.PurchaseItemReturn";
    private const string FinanceTag = "FullWorth.Backend.Modules.Purchases.FinanceTag";
    private const string PurchaseTagLink = "FullWorth.Backend.Modules.Purchases.PurchaseTagLink";
    private const string Transaction = "FullWorth.Backend.Modules.Transactions.FinanceTransaction";
    private const string TransactionAllocation = "FullWorth.Backend.Modules.Transactions.TransactionAllocation";
    private const string Category = "FullWorth.Backend.Modules.Categories.FinanceCategory";
    private const string Space = "FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpace";
    private const string Merchant = "FullWorth.Backend.Modules.Merchants.Merchant";

    internal static void Apply(ModelBuilder modelBuilder)
    {
        ExtendPurchase(modelBuilder);
        ExtendPurchaseItem(modelBuilder);
        ExtendTransactionAllocation(modelBuilder);
        AddProductModel(modelBuilder);
        AddPaymentModel(modelBuilder);
        AddDiscountModel(modelBuilder);
        AddAllocationProvenanceModel(modelBuilder);
        AddDocumentModel(modelBuilder);
        AddMetadataModel(modelBuilder);
    }

    private static void ExtendPurchase(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Purchase, b =>
        {
            b.Property<Guid?>("MerchantId").HasColumnType("uuid");
            b.Property<string>("MerchantRaw").HasMaxLength(250).HasColumnType("character varying(250)");
            b.Property<TimeOnly?>("PurchaseTime").HasColumnType("time without time zone");
            b.Property<string>("TimeZone").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<decimal?>("SubtotalAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("DiscountAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("DepositAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal>("RoundingAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("TaxAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("TipAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("ShippingAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("FeeAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("ReviewState").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("ReceiptNumber").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("InvoiceNumber").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("PaymentMethodText").HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<bool>("IsBookmarked").HasColumnType("boolean");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<Guid?>("PaidByUserId").HasColumnType("uuid");
            b.Property<Guid?>("ForWhomUserId").HasColumnType("uuid");
            b.Property<string>("Visibility").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");

            b.HasIndex("MerchantId");
            b.HasIndex("FullWorthSpaceId", "PurchaseDate");
            b.HasIndex("FullWorthSpaceId", "MerchantId");
            b.HasIndex("FullWorthSpaceId", "ReviewState");

            b.HasOne(Merchant, null).WithMany().HasForeignKey("MerchantId").OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ExtendPurchaseItem(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(PurchaseItem, b =>
        {
            b.Property<Guid?>("ProductId").HasColumnType("uuid");
            b.Property<string>("RawName").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("Barcode").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("QuantityUnit").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<decimal?>("PackageQuantity").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<string>("PackageUnit").HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<decimal?>("PackageCount").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<decimal?>("OriginalUnitPrice").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("BaseUnitPrice").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("DiscountAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("DiscountLabel").HasMaxLength(250).HasColumnType("character varying(250)");
            b.Property<decimal?>("DepositAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("TaxRate").HasPrecision(8, 4).HasColumnType("numeric(8,4)");
            b.Property<decimal?>("TaxAmount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("LineType").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<decimal?>("ExtractionConfidence").HasPrecision(5, 4).HasColumnType("numeric(5,4)");
            b.Property<bool>("IsManuallyCorrected").HasColumnType("boolean");
            b.Property<bool>("TotalPriceOverridden").HasColumnType("boolean");
            b.Property<int>("SortOrder").HasColumnType("integer");
            b.Property<DateOnly?>("ReturnDeadline").HasColumnType("date");
            b.Property<DateOnly?>("WarrantyEnd").HasColumnType("date");
            b.Property<string>("SerialNumber").HasMaxLength(200).HasColumnType("character varying(200)");

            b.HasIndex("ProductId");
            b.HasIndex("PurchaseId", "SortOrder");
            b.HasOne(Product, "Product").WithMany("PurchaseItems").HasForeignKey("ProductId").OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void ExtendTransactionAllocation(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(TransactionAllocation, b =>
        {
            // PurchaseItemId already existed as a scalar in the generated baseline, but had no FK/index.
            b.HasIndex("PurchaseItemId");
            b.HasOne(PurchaseItem, null).WithMany().HasForeignKey("PurchaseItemId").OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void AddProductModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Product, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<string>("CanonicalName").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("Brand").HasMaxLength(250).HasColumnType("character varying(250)");
            b.Property<Guid?>("DefaultCategoryId").HasColumnType("uuid");
            b.Property<string>("DefaultQuantityUnit").HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<decimal?>("DefaultPackageQuantity").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<string>("DefaultPackageUnit").HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("ImageReference").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("Notes").HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<bool>("IsArchived").HasColumnType("boolean");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("DefaultCategoryId");
            b.HasIndex("FullWorthSpaceId", "CanonicalName");
            b.ToTable("Products");
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Category, null).WithMany().HasForeignKey("DefaultCategoryId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(ProductAlias, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("ProductId").HasColumnType("uuid");
            b.Property<Guid?>("MerchantId").HasColumnType("uuid");
            b.Property<string>("Alias").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("NormalizedAlias").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("AliasType").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("MerchantId");
            b.HasIndex("ProductId", "NormalizedAlias");
            b.ToTable("ProductAliases");
            b.HasOne(Product, "Product").WithMany("Aliases").HasForeignKey("ProductId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Merchant, null).WithMany().HasForeignKey("MerchantId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(ProductBarcode, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("ProductId").HasColumnType("uuid");
            b.Property<string>("Code").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("Standard").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("ProductId");
            b.HasIndex("Code").IsUnique();
            b.ToTable("ProductBarcodes");
            b.HasOne(Product, "Product").WithMany("Barcodes").HasForeignKey("ProductId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
    }

    private static void AddPaymentModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(PaymentLink, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<Guid>("PurchaseId").HasColumnType("uuid");
            b.Property<Guid>("TransactionId").HasColumnType("uuid");
            b.Property<decimal>("Amount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<string>("LinkSource").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<decimal?>("Confidence").HasPrecision(5, 4).HasColumnType("numeric(5,4)");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("FullWorthSpaceId");
            b.HasIndex("PurchaseId");
            b.HasIndex("TransactionId");
            b.HasIndex("PurchaseId", "TransactionId").IsUnique();
            b.ToTable("PurchasePaymentLinks");
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne(Purchase, "Purchase").WithMany("PaymentLinks").HasForeignKey("PurchaseId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Transaction, null).WithMany().HasForeignKey("TransactionId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
    }

    private static void AddDiscountModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(PurchaseDiscount, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("PurchaseId").HasColumnType("uuid");
            b.Property<Guid?>("PurchaseItemId").HasColumnType("uuid");
            b.Property<string>("Type").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Label").IsRequired().HasMaxLength(250).HasColumnType("character varying(250)");
            b.Property<decimal>("Amount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<decimal?>("Percentage").HasPrecision(8, 4).HasColumnType("numeric(8,4)");
            b.Property<string>("CouponCode").HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<string>("RawText").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("Source").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<decimal?>("Confidence").HasPrecision(5, 4).HasColumnType("numeric(5,4)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("PurchaseItemId");
            b.HasIndex("PurchaseId", "CreatedAt");
            b.ToTable("PurchaseDiscounts");
            b.HasOne(Purchase, "Purchase").WithMany("Discounts").HasForeignKey("PurchaseId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(PurchaseItem, "PurchaseItem").WithMany().HasForeignKey("PurchaseItemId").OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void AddAllocationProvenanceModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(PurchaseAllocationLink, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("TransactionAllocationId").HasColumnType("uuid");
            b.Property<Guid>("PurchaseId").HasColumnType("uuid");
            b.Property<Guid?>("PurchaseDiscountId").HasColumnType("uuid");
            b.Property<string>("AllocationType").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("PurchaseDiscountId");
            b.HasIndex("PurchaseId");
            b.HasIndex("TransactionAllocationId").IsUnique();
            b.ToTable("PurchaseAllocationLinks");
            b.HasOne(TransactionAllocation, "TransactionAllocation").WithMany().HasForeignKey("TransactionAllocationId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Purchase, "Purchase").WithMany().HasForeignKey("PurchaseId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(PurchaseDiscount, "PurchaseDiscount").WithMany("AllocationLinks").HasForeignKey("PurchaseDiscountId").OnDelete(DeleteBehavior.SetNull);
        });
    }

    private static void AddDocumentModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(Document, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("PurchaseId").HasColumnType("uuid");
            b.Property<string>("DocumentType").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("OriginalFileName").IsRequired().HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("MediaType").IsRequired().HasMaxLength(150).HasColumnType("character varying(150)");
            b.Property<string>("StoragePath").IsRequired().HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("Sha256").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("PerceptualHash").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<int?>("PageCount").HasColumnType("integer");
            b.Property<long>("SizeBytes").HasColumnType("bigint");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("PurchaseId");
            b.HasIndex("Sha256");
            b.ToTable("PurchaseDocuments");
            b.HasOne(Purchase, "Purchase").WithMany("Documents").HasForeignKey("PurchaseId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });

        modelBuilder.Entity(ExtractionRun, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("PurchaseDocumentId").HasColumnType("uuid");
            b.Property<string>("Provider").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("ProviderVersion").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("StartedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("ErrorCode").HasMaxLength(80).HasColumnType("character varying(80)");
            b.Property<string>("ErrorMessageSafe").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<string>("RawResultJson").HasColumnType("text");
            b.Property<string>("NormalizedResultJson").HasColumnType("text");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("PurchaseDocumentId", "CreatedAt");
            b.ToTable("PurchaseExtractionRuns");
            b.HasOne(Document, "PurchaseDocument").WithMany("ExtractionRuns").HasForeignKey("PurchaseDocumentId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
    }

    private static void AddMetadataModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity(DifferenceAcceptance, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("PurchaseId").HasColumnType("uuid");
            b.Property<string>("Kind").IsRequired().HasMaxLength(16).HasColumnType("character varying(16)");
            b.Property<decimal>("Amount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("Reason").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Note").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<Guid>("AcceptedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("AcceptedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("PurchaseId", "Kind").IsUnique();
            b.ToTable("PurchaseDifferenceAcceptances");
            b.HasOne(Purchase, "Purchase").WithMany("AcceptedDifferences").HasForeignKey("PurchaseId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });

        modelBuilder.Entity(ItemReturn, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("PurchaseItemId").HasColumnType("uuid");
            b.Property<Guid?>("RefundTransactionId").HasColumnType("uuid");
            b.Property<decimal>("Quantity").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<decimal>("Amount").HasPrecision(20, 8).HasColumnType("numeric(20,8)");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)");
            b.Property<string>("Status").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<string>("Note").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<Guid>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("PurchaseItemId");
            b.HasIndex("RefundTransactionId");
            b.ToTable("PurchaseItemReturns");
            b.HasOne(PurchaseItem, "PurchaseItem").WithMany("Returns").HasForeignKey("PurchaseItemId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(Transaction, null).WithMany().HasForeignKey("RefundTransactionId").OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity(FinanceTag, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("FullWorthSpaceId").HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("NormalizedName").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("Color").HasMaxLength(9).HasColumnType("character varying(9)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("FullWorthSpaceId", "NormalizedName").IsUnique();
            b.ToTable("FinanceTags");
            b.HasOne(Space, null).WithMany().HasForeignKey("FullWorthSpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });

        modelBuilder.Entity(PurchaseTagLink, b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<Guid>("PurchaseId").HasColumnType("uuid");
            b.Property<Guid>("TagId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("TagId");
            b.HasIndex("PurchaseId", "TagId").IsUnique();
            b.ToTable("PurchaseTagLinks");
            b.HasOne(Purchase, "Purchase").WithMany("Tags").HasForeignKey("PurchaseId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne(FinanceTag, "Tag").WithMany("PurchaseLinks").HasForeignKey("TagId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
    }
}
