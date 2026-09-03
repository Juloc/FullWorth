using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

[DbContext(typeof(FullWorthDbContext))]
[Migration("20260830133000_PurchasesArticlesSystem")]
public sealed class PurchasesArticlesSystem : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "MerchantId", table: "Purchases", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "MerchantRaw", table: "Purchases", type: "character varying(250)", maxLength: 250, nullable: true);
        migrationBuilder.AddColumn<TimeOnly>(name: "PurchaseTime", table: "Purchases", type: "time without time zone", nullable: true);
        migrationBuilder.AddColumn<string>(name: "TimeZone", table: "Purchases", type: "character varying(100)", maxLength: 100, nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "SubtotalAmount", table: "Purchases", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "DiscountAmount", table: "Purchases", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "DepositAmount", table: "Purchases", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "TaxAmount", table: "Purchases", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "TipAmount", table: "Purchases", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "ShippingAmount", table: "Purchases", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "FeeAmount", table: "Purchases", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "ReviewState", table: "Purchases", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "needs_review");
        migrationBuilder.AddColumn<string>(name: "ReceiptNumber", table: "Purchases", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "InvoiceNumber", table: "Purchases", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "PaymentMethodText", table: "Purchases", type: "character varying(120)", maxLength: 120, nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsBookmarked", table: "Purchases", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<Guid>(name: "CreatedByUserId", table: "Purchases", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "PaidByUserId", table: "Purchases", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "ForWhomUserId", table: "Purchases", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "Visibility", table: "Purchases", type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "space");

        migrationBuilder.AddColumn<Guid>(name: "ProductId", table: "PurchaseItems", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<string>(name: "RawName", table: "PurchaseItems", type: "character varying(500)", maxLength: 500, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "Barcode", table: "PurchaseItems", type: "character varying(64)", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<string>(name: "QuantityUnit", table: "PurchaseItems", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "piece");
        migrationBuilder.AddColumn<decimal>(name: "PackageQuantity", table: "PurchaseItems", type: "numeric(20,6)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "PackageUnit", table: "PurchaseItems", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "PackageCount", table: "PurchaseItems", type: "numeric(20,6)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "BaseUnitPrice", table: "PurchaseItems", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "DiscountAmount", table: "PurchaseItems", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "DepositAmount", table: "PurchaseItems", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "TaxRate", table: "PurchaseItems", type: "numeric(8,4)", nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "TaxAmount", table: "PurchaseItems", type: "numeric(20,8)", nullable: true);
        migrationBuilder.AddColumn<string>(name: "LineType", table: "PurchaseItems", type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "product");
        migrationBuilder.AddColumn<decimal>(name: "ExtractionConfidence", table: "PurchaseItems", type: "numeric(5,4)", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "IsManuallyCorrected", table: "PurchaseItems", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(name: "TotalPriceOverridden", table: "PurchaseItems", type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<int>(name: "SortOrder", table: "PurchaseItems", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateOnly>(name: "ReturnDeadline", table: "PurchaseItems", type: "date", nullable: true);
        migrationBuilder.AddColumn<DateOnly>(name: "WarrantyEnd", table: "PurchaseItems", type: "date", nullable: true);
        migrationBuilder.AddColumn<string>(name: "SerialNumber", table: "PurchaseItems", type: "character varying(200)", maxLength: 200, nullable: true);

        migrationBuilder.CreateTable(
            name: "Products",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                CanonicalName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                Brand = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                DefaultCategoryId = table.Column<Guid>(type: "uuid", nullable: true),
                DefaultQuantityUnit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                DefaultPackageQuantity = table.Column<decimal>(type: "numeric(20,6)", nullable: true),
                DefaultPackageUnit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                ImageReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Products", x => x.Id));

        migrationBuilder.CreateTable(
            name: "PurchasePaymentLinks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                LinkSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchasePaymentLinks", x => x.Id);
                table.ForeignKey("FK_PurchasePaymentLinks_Purchases_PurchaseId", x => x.PurchaseId, "Purchases", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PurchasePaymentLinks_Transactions_TransactionId", x => x.TransactionId, "Transactions", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseDocuments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                OriginalFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                MediaType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                StoragePath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PerceptualHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                PageCount = table.Column<int>(type: "integer", nullable: true),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseDocuments", x => x.Id);
                table.ForeignKey("FK_PurchaseDocuments_Purchases_PurchaseId", x => x.PurchaseId, "Purchases", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseDifferenceAcceptances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseDifferenceAcceptances", x => x.Id);
                table.ForeignKey("FK_PurchaseDifferenceAcceptances_Purchases_PurchaseId", x => x.PurchaseId, "Purchases", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "FinanceTags",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FullWorthSpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                NormalizedName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Color = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_FinanceTags", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ProductAliases",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                MerchantId = table.Column<Guid>(type: "uuid", nullable: true),
                Alias = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                NormalizedAlias = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                AliasType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductAliases", x => x.Id);
                table.ForeignKey("FK_ProductAliases_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ProductBarcodes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Standard = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductBarcodes", x => x.Id);
                table.ForeignKey("FK_ProductBarcodes_Products_ProductId", x => x.ProductId, "Products", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseExtractionRuns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ProviderVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                ErrorMessageSafe = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                RawResultJson = table.Column<string>(type: "text", nullable: true),
                NormalizedResultJson = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseExtractionRuns", x => x.Id);
                table.ForeignKey("FK_PurchaseExtractionRuns_PurchaseDocuments_PurchaseDocumentId", x => x.PurchaseDocumentId, "PurchaseDocuments", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseItemReturns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseItemId = table.Column<Guid>(type: "uuid", nullable: false),
                RefundTransactionId = table.Column<Guid>(type: "uuid", nullable: true),
                Quantity = table.Column<decimal>(type: "numeric(20,6)", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseItemReturns", x => x.Id);
                table.ForeignKey("FK_PurchaseItemReturns_PurchaseItems_PurchaseItemId", x => x.PurchaseItemId, "PurchaseItems", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PurchaseItemReturns_Transactions_RefundTransactionId", x => x.RefundTransactionId, "Transactions", "Id", onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "PurchaseTagLinks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseId = table.Column<Guid>(type: "uuid", nullable: false),
                TagId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PurchaseTagLinks", x => x.Id);
                table.ForeignKey("FK_PurchaseTagLinks_Purchases_PurchaseId", x => x.PurchaseId, "Purchases", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_PurchaseTagLinks_FinanceTags_TagId", x => x.TagId, "FinanceTags", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("UPDATE \"PurchaseItems\" SET \"RawName\" = \"Name\" WHERE \"RawName\" = '';");
        migrationBuilder.Sql("UPDATE \"Purchases\" SET \"ReviewState\" = CASE WHEN \"Status\" = 'confirmed' THEN 'confirmed' ELSE 'needs_review' END;");

        migrationBuilder.Sql("""
            INSERT INTO "PurchasePaymentLinks" ("Id", "FullWorthSpaceId", "PurchaseId", "TransactionId", "Amount", "Currency", "LinkSource", "Confidence", "CreatedByUserId", "CreatedAt", "UpdatedAt")
            SELECT p."Id", p."FullWorthSpaceId", p."Id", p."TransactionId", ABS(p."TotalAmount"), p."Currency", 'legacy', p."MatchConfidence", NULL, p."CreatedAt", p."UpdatedAt"
            FROM "Purchases" p
            WHERE p."TransactionId" IS NOT NULL
            ON CONFLICT ("Id") DO NOTHING;
            """);

        migrationBuilder.Sql("""
            INSERT INTO "PurchaseDocuments" ("Id", "PurchaseId", "DocumentType", "OriginalFileName", "MediaType", "StoragePath", "Sha256", "PerceptualHash", "PageCount", "SizeBytes", "Status", "CreatedAt", "UpdatedAt")
            SELECT p."Id", p."Id", 'receipt', regexp_replace(p."ReceiptImagePath", '^.*/', ''), 'application/octet-stream', p."ReceiptImagePath", '', NULL, NULL, 0, 'confirmed', p."CreatedAt", p."UpdatedAt"
            FROM "Purchases" p
            WHERE p."ReceiptImagePath" IS NOT NULL
            ON CONFLICT ("Id") DO NOTHING;
            """);

        migrationBuilder.CreateIndex(name: "IX_Purchases_FullWorthSpaceId_PurchaseDate", table: "Purchases", columns: new[] { "FullWorthSpaceId", "PurchaseDate" });
        migrationBuilder.CreateIndex(name: "IX_Purchases_FullWorthSpaceId_MerchantId", table: "Purchases", columns: new[] { "FullWorthSpaceId", "MerchantId" });
        migrationBuilder.CreateIndex(name: "IX_Purchases_FullWorthSpaceId_ReviewState", table: "Purchases", columns: new[] { "FullWorthSpaceId", "ReviewState" });
        migrationBuilder.CreateIndex(name: "IX_PurchaseItems_ProductId", table: "PurchaseItems", column: "ProductId");
        migrationBuilder.CreateIndex(name: "IX_PurchaseItems_PurchaseId_SortOrder", table: "PurchaseItems", columns: new[] { "PurchaseId", "SortOrder" });
        migrationBuilder.CreateIndex(name: "IX_Products_FullWorthSpaceId_CanonicalName", table: "Products", columns: new[] { "FullWorthSpaceId", "CanonicalName" });
        migrationBuilder.CreateIndex(name: "IX_ProductAliases_ProductId_NormalizedAlias", table: "ProductAliases", columns: new[] { "ProductId", "NormalizedAlias" });
        migrationBuilder.CreateIndex(name: "IX_ProductBarcodes_Code", table: "ProductBarcodes", column: "Code", unique: true);
        migrationBuilder.CreateIndex(name: "IX_PurchasePaymentLinks_PurchaseId", table: "PurchasePaymentLinks", column: "PurchaseId");
        migrationBuilder.CreateIndex(name: "IX_PurchasePaymentLinks_TransactionId", table: "PurchasePaymentLinks", column: "TransactionId");
        migrationBuilder.CreateIndex(name: "IX_PurchasePaymentLinks_PurchaseId_TransactionId", table: "PurchasePaymentLinks", columns: new[] { "PurchaseId", "TransactionId" });
        migrationBuilder.CreateIndex(name: "IX_PurchaseDocuments_PurchaseId", table: "PurchaseDocuments", column: "PurchaseId");
        migrationBuilder.CreateIndex(name: "IX_PurchaseDocuments_Sha256", table: "PurchaseDocuments", column: "Sha256");
        migrationBuilder.CreateIndex(name: "IX_PurchaseExtractionRuns_PurchaseDocumentId_CreatedAt", table: "PurchaseExtractionRuns", columns: new[] { "PurchaseDocumentId", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_PurchaseDifferenceAcceptances_PurchaseId_Kind", table: "PurchaseDifferenceAcceptances", columns: new[] { "PurchaseId", "Kind" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_PurchaseItemReturns_PurchaseItemId", table: "PurchaseItemReturns", column: "PurchaseItemId");
        migrationBuilder.CreateIndex(name: "IX_PurchaseItemReturns_RefundTransactionId", table: "PurchaseItemReturns", column: "RefundTransactionId");
        migrationBuilder.CreateIndex(name: "IX_FinanceTags_FullWorthSpaceId_NormalizedName", table: "FinanceTags", columns: new[] { "FullWorthSpaceId", "NormalizedName" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_PurchaseTagLinks_PurchaseId_TagId", table: "PurchaseTagLinks", columns: new[] { "PurchaseId", "TagId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_PurchaseTagLinks_TagId", table: "PurchaseTagLinks", column: "TagId");

        migrationBuilder.AddForeignKey(name: "FK_PurchaseItems_Products_ProductId", table: "PurchaseItems", column: "ProductId", principalTable: "Products", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_PurchaseItems_Products_ProductId", table: "PurchaseItems");
        migrationBuilder.DropTable(name: "ProductAliases");
        migrationBuilder.DropTable(name: "ProductBarcodes");
        migrationBuilder.DropTable(name: "PurchaseDifferenceAcceptances");
        migrationBuilder.DropTable(name: "PurchaseExtractionRuns");
        migrationBuilder.DropTable(name: "PurchaseItemReturns");
        migrationBuilder.DropTable(name: "PurchasePaymentLinks");
        migrationBuilder.DropTable(name: "PurchaseTagLinks");
        migrationBuilder.DropTable(name: "PurchaseDocuments");
        migrationBuilder.DropTable(name: "Products");
        migrationBuilder.DropTable(name: "FinanceTags");

        foreach (var column in new[] { "ProductId", "RawName", "Barcode", "QuantityUnit", "PackageQuantity", "PackageUnit", "PackageCount", "BaseUnitPrice", "DiscountAmount", "DepositAmount", "TaxRate", "TaxAmount", "LineType", "ExtractionConfidence", "IsManuallyCorrected", "TotalPriceOverridden", "SortOrder", "ReturnDeadline", "WarrantyEnd", "SerialNumber" })
            migrationBuilder.DropColumn(name: column, table: "PurchaseItems");
        foreach (var column in new[] { "MerchantId", "MerchantRaw", "PurchaseTime", "TimeZone", "SubtotalAmount", "DiscountAmount", "DepositAmount", "TaxAmount", "TipAmount", "ShippingAmount", "FeeAmount", "ReviewState", "ReceiptNumber", "InvoiceNumber", "PaymentMethodText", "IsBookmarked", "CreatedByUserId", "PaidByUserId", "ForWhomUserId", "Visibility" })
            migrationBuilder.DropColumn(name: column, table: "Purchases");
    }
}
