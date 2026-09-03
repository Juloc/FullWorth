using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Contracts.PriceChanges;
using FullWorth.Backend.Modules.Contracts.Review;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Notifications;
using FullWorth.Backend.Modules.Push;
using FullWorth.Backend.Modules.Tax;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FullWorth.Backend.Data;

public sealed class FullWorthDbContext(DbContextOptions<FullWorthDbContext> options) : DbContext(options)
{
    public DbSet<FullWorthUser> Users => Set<FullWorthUser>();
    public DbSet<FullWorthSpace> FullWorthSpaces => Set<FullWorthSpace>();
    public DbSet<FullWorthSpaceMember> FullWorthSpaceMembers => Set<FullWorthSpaceMember>();
    public DbSet<FullWorthSpaceInvite> FullWorthSpaceInvites => Set<FullWorthSpaceInvite>();
    public DbSet<AccountOwner> AccountOwners => Set<AccountOwner>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<FinanceAccount> Accounts => Set<FinanceAccount>();
    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();
    public DbSet<BalanceSnapshot> BalanceSnapshots => Set<BalanceSnapshot>();
    public DbSet<FinanceTransaction> Transactions => Set<FinanceTransaction>();
    public DbSet<TransactionAllocation> TransactionAllocations => Set<TransactionAllocation>();
    public DbSet<FinanceCategory> Categories => Set<FinanceCategory>();
    public DbSet<CategorizationRule> CategorizationRules => Set<CategorizationRule>();
    public DbSet<RecurringContract> Contracts => Set<RecurringContract>();
    public DbSet<PriceChangeSuggestion> PriceChangeSuggestions => Set<PriceChangeSuggestion>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Liability> Liabilities => Set<Liability>();
    public DbSet<NetWorthSnapshot> NetWorthSnapshots => Set<NetWorthSnapshot>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<PurchaseItem> PurchaseItems => Set<PurchaseItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductAlias> ProductAliases => Set<ProductAlias>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<PurchasePaymentLink> PurchasePaymentLinks => Set<PurchasePaymentLink>();
    public DbSet<PurchaseDocument> PurchaseDocuments => Set<PurchaseDocument>();
    public DbSet<PurchaseExtractionRun> PurchaseExtractionRuns => Set<PurchaseExtractionRun>();
    public DbSet<PurchaseDifferenceAcceptance> PurchaseDifferenceAcceptances => Set<PurchaseDifferenceAcceptance>();
    public DbSet<PurchaseItemReturn> PurchaseItemReturns => Set<PurchaseItemReturn>();
    public DbSet<FinanceTag> FinanceTags => Set<FinanceTag>();
    public DbSet<PurchaseTagLink> PurchaseTagLinks => Set<PurchaseTagLink>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<MerchantAlias> MerchantAliases => Set<MerchantAlias>();
    public DbSet<DismissedContractCandidate> DismissedContractCandidates => Set<DismissedContractCandidate>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<NotificationDedup> NotificationDedups => Set<NotificationDedup>();
    public DbSet<TaxSettings> TaxSettings => Set<TaxSettings>();
    public DbSet<TaxProfile> TaxProfiles => Set<TaxProfile>();
    public DbSet<TaxCategory> TaxCategories => Set<TaxCategory>();
    public DbSet<TaxCandidate> TaxCandidates => Set<TaxCandidate>();
    public DbSet<TaxCandidateSource> TaxCandidateSources => Set<TaxCandidateSource>();
    public DbSet<TaxRuleDefinition> TaxRuleDefinitions => Set<TaxRuleDefinition>();
    public DbSet<TaxUserMapping> TaxUserMappings => Set<TaxUserMapping>();
    public DbSet<TaxFeedback> TaxFeedback => Set<TaxFeedback>();
    public DbSet<TaxAnalysisRun> TaxAnalysisRuns => Set<TaxAnalysisRun>();
    public DbSet<FullWorth.Backend.Modules.Preferences.UserPreference> UserPreferences => Set<FullWorth.Backend.Modules.Preferences.UserPreference>();
    public DbSet<FullWorth.Backend.Modules.Fx.FxRate> FxRates => Set<FullWorth.Backend.Modules.Fx.FxRate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<FullWorth.Backend.Modules.Preferences.UserPreference>(e =>
        {
            e.HasIndex(x => new { x.FinanceUserId, x.FullWorthSpaceId, x.Key }).IsUnique();
            e.Property(x => x.Key).HasMaxLength(80);
            e.Property(x => x.ValueJson).HasColumnType("jsonb");
        });
        b.Entity<FullWorth.Backend.Modules.Fx.FxRate>(e =>
        {
            e.HasIndex(x => new { x.Date, x.Currency }).IsUnique();
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Rate).HasPrecision(20, 10);
        });
        b.ApplyConfiguration(new FullWorthUserConfiguration());
        b.ApplyConfiguration(new FullWorthSpaceConfiguration());
        b.ApplyConfiguration(new FullWorthSpaceMemberConfiguration());
        b.ApplyConfiguration(new FullWorthSpaceInviteConfiguration());
        b.ApplyConfiguration(new AccountOwnerConfiguration());
        b.ApplyConfiguration(new AuditEventConfiguration());
        b.ApplyConfiguration(new MerchantConfiguration());
        b.ApplyConfiguration(new MerchantAliasConfiguration());

        b.Entity<BankConnection>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.ProviderSessionIdLookup }).IsUnique();
            e.HasIndex(x => x.AuthorizationState).IsUnique();
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.AuthorizationState).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FinanceAccount>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.IdentificationHash });
            e.HasIndex(x => new { x.FullWorthSpaceId, x.Provider, x.IdentificationHash }).IsUnique();
            e.HasIndex(x => x.BankConnectionId);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.IbanLast4).HasMaxLength(4);
            e.HasIndex(x => x.GroupId);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BankConnection>().WithMany().HasForeignKey(x => x.BankConnectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AccountGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<BalanceSnapshot>(e =>
        {
            e.HasIndex(x => new { x.AccountId, x.CapturedAt });
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<FinanceTransaction>(e =>
        {
            e.HasIndex(x => new { x.AccountId, x.ExternalKey }).IsUnique();
            e.HasIndex(x => x.BookingDate);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.NormalizedCounterparty);
            e.HasIndex(x => x.TransferGroupId);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.RawJson).HasColumnType("text");
            e.Property(x => x.UserNote).HasMaxLength(2000);
            e.Property(x => x.TransferPurpose).HasMaxLength(80);
            e.HasIndex(x => x.RefundOfTransactionId);
            e.HasIndex(x => x.RefundCategoryId);
            e.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FinanceTransaction>().WithMany().HasForeignKey(x => x.RefundOfTransactionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.RefundCategoryId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TransactionAllocation>(e =>
        {
            e.HasIndex(x => x.TransactionId);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.PurchaseItemId);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<FinanceTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            // Article detail may be deleted while its financial split must survive. SetNull preserves
            // amount/category and turns the line back into a generic allocation.
            e.HasOne<PurchaseItem>().WithMany().HasForeignKey(x => x.PurchaseItemId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<FinanceCategory>(e =>
        {
            e.HasIndex(x => x.Key);
            e.HasIndex(x => new { x.FullWorthSpaceId, x.Key }).IsUnique();
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CategorizationRule>(e =>
        {
            e.HasIndex(x => new { x.Target, x.IsEnabled, x.Priority });
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.Target).HasMaxLength(32);
            e.Property(x => x.MatchField).HasMaxLength(64);
            e.Property(x => x.MatchMode).HasMaxLength(32);
            e.Property(x => x.MinAmount).HasPrecision(20, 8);
            e.Property(x => x.MaxAmount).HasPrecision(20, 8);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RecurringContract>(e =>
        {
            e.HasIndex(x => new { x.IsActive, x.NextDueDate });
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Budget>(e =>
        {
            e.HasIndex(x => new { x.IsActive, x.CategoryId });
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PriceChangeSuggestion>(e =>
        {
            e.HasIndex(x => x.ContractId);
            e.HasIndex(x => x.EvidenceTransactionId);
            e.Property(x => x.OldAmount).HasPrecision(20, 8);
            e.Property(x => x.NewAmount).HasPrecision(20, 8);
            e.Property(x => x.PercentChange).HasPrecision(20, 8);
            e.Property(x => x.Status).HasMaxLength(16);
            e.HasOne(x => x.Contract).WithMany().HasForeignKey(x => x.ContractId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.EvidenceTransaction).WithMany().HasForeignKey(x => x.EvidenceTransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DismissedContractCandidate>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.Counterparty, x.Currency }).IsUnique();
            e.Property(x => x.Counterparty).HasMaxLength(512);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PushDevice>(e =>
        {
            e.HasIndex(x => new { x.FinanceUserId, x.Endpoint }).IsUnique();
            e.Property(x => x.Endpoint).HasMaxLength(500);
            e.Property(x => x.P256dh).HasMaxLength(256);
            e.Property(x => x.Auth).HasMaxLength(256);
            e.Property(x => x.DeviceLabel).HasMaxLength(120);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.FinanceUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NotificationDedup>(e =>
        {
            e.HasIndex(x => new { x.FinanceUserId, x.Type, x.DedupKey }).IsUnique();
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.Type).HasMaxLength(40);
            e.Property(x => x.DedupKey).HasMaxLength(200);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.FinanceUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Loan>(e =>
        {
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.OriginalPrincipal).HasPrecision(20, 8);
            e.Property(x => x.CurrentBalance).HasPrecision(20, 8);
            e.Property(x => x.PaymentAmount).HasPrecision(20, 8);
            e.Property(x => x.NominalInterestRate).HasPrecision(20, 8);
            e.Property(x => x.Fees).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<FinanceAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Asset>(e =>
        {
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.CurrentValue).HasPrecision(20, 8);
            e.Property(x => x.AnnualGrowthRate).HasPrecision(10, 6);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Liability>(e =>
        {
            e.HasIndex(x => x.FullWorthSpaceId);
            e.Property(x => x.CurrentBalance).HasPrecision(20, 8);
            e.Property(x => x.InterestRate).HasPrecision(10, 6);
            e.Property(x => x.RegularPayment).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<NetWorthSnapshot>(e =>
        {
            e.HasIndex(x => new { x.Date, x.Currency });
            e.HasIndex(x => new { x.FullWorthSpaceId, x.UserId, x.Date, x.Currency }).IsUnique();
            e.HasIndex(x => new { x.FullWorthSpaceId, x.UserId });
            e.Property(x => x.Accounts).HasPrecision(20, 8);
            e.Property(x => x.Assets).HasPrecision(20, 8);
            e.Property(x => x.Liabilities).HasPrecision(20, 8);
            e.Property(x => x.NetWorth).HasPrecision(20, 8);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FullWorthUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Purchase>(e =>
        {
            e.HasIndex(x => x.TransactionId);
            e.HasIndex(x => new { x.Source, x.ExternalOrderId });
            e.HasIndex(x => new { x.FullWorthSpaceId, x.Source, x.ExternalOrderId }).IsUnique();
            e.HasIndex(x => x.PurchaseDate);
            e.HasIndex(x => x.FullWorthSpaceId);
            e.HasIndex(x => new { x.FullWorthSpaceId, x.PurchaseDate });
            e.HasIndex(x => new { x.FullWorthSpaceId, x.MerchantId });
            e.HasIndex(x => new { x.FullWorthSpaceId, x.ReviewState });
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.Merchant).HasMaxLength(250);
            e.Property(x => x.MerchantRaw).HasMaxLength(250);
            e.Property(x => x.ExternalOrderId).HasMaxLength(200);
            e.Property(x => x.TimeZone).HasMaxLength(100);
            e.Property(x => x.SubtotalAmount).HasPrecision(20, 8);
            e.Property(x => x.DiscountAmount).HasPrecision(20, 8);
            e.Property(x => x.DepositAmount).HasPrecision(20, 8);
            e.Property(x => x.TaxAmount).HasPrecision(20, 8);
            e.Property(x => x.TipAmount).HasPrecision(20, 8);
            e.Property(x => x.ShippingAmount).HasPrecision(20, 8);
            e.Property(x => x.FeeAmount).HasPrecision(20, 8);
            e.Property(x => x.TotalAmount).HasPrecision(20, 8);
            e.Property(x => x.RoundingAmount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.ReviewState).HasMaxLength(32);
            e.Property(x => x.MatchConfidence).HasPrecision(5, 4);
            e.Property(x => x.ReceiptImagePath).HasMaxLength(1000);
            e.Property(x => x.ReceiptNumber).HasMaxLength(200);
            e.Property(x => x.InvoiceNumber).HasMaxLength(200);
            e.Property(x => x.PaymentMethodText).HasMaxLength(120);
            e.Property(x => x.SourceReference).HasMaxLength(1000);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Visibility).HasMaxLength(16);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Items).WithOne(x => x.Purchase).HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.PaymentLinks).WithOne(x => x.Purchase).HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Documents).WithOne(x => x.Purchase).HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.AcceptedDifferences).WithOne(x => x.Purchase).HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tags).WithOne(x => x.Purchase).HasForeignKey(x => x.PurchaseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PurchaseItem>(e =>
        {
            e.HasIndex(x => x.PurchaseId);
            e.HasIndex(x => new { x.PurchaseId, x.SortOrder });
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.ProductId);
            e.HasIndex(x => x.Asin);
            e.HasIndex(x => x.Sku);
            e.Property(x => x.RawName).HasMaxLength(500);
            e.Property(x => x.Name).HasMaxLength(500);
            e.Property(x => x.Brand).HasMaxLength(250);
            e.Property(x => x.Sku).HasMaxLength(200);
            e.Property(x => x.Barcode).HasMaxLength(64);
            e.Property(x => x.Asin).HasMaxLength(32);
            e.Property(x => x.Quantity).HasPrecision(20, 6);
            e.Property(x => x.QuantityUnit).HasMaxLength(32);
            e.Property(x => x.PackageQuantity).HasPrecision(20, 6);
            e.Property(x => x.PackageUnit).HasMaxLength(32);
            e.Property(x => x.PackageCount).HasPrecision(20, 6);
            e.Property(x => x.UnitPrice).HasPrecision(20, 8);
            e.Property(x => x.BaseUnitPrice).HasPrecision(20, 8);
            e.Property(x => x.TotalPrice).HasPrecision(20, 8);
            e.Property(x => x.OriginalUnitPrice).HasPrecision(20, 8);
            e.Property(x => x.DiscountAmount).HasPrecision(20, 8);
            e.Property(x => x.DiscountLabel).HasMaxLength(250);
            e.Property(x => x.DepositAmount).HasPrecision(20, 8);
            e.Property(x => x.TaxRate).HasPrecision(8, 4);
            e.Property(x => x.TaxAmount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.LineType).HasMaxLength(32);
            e.Property(x => x.CategorizationSource).HasMaxLength(32);
            e.Property(x => x.ExtractionConfidence).HasPrecision(5, 4);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.SerialNumber).HasMaxLength(200);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Product).WithMany(x => x.PurchaseItems).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Returns).WithOne(x => x.PurchaseItem).HasForeignKey(x => x.PurchaseItemId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Product>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.CanonicalName });
            e.Property(x => x.CanonicalName).HasMaxLength(500);
            e.Property(x => x.Brand).HasMaxLength(250);
            e.Property(x => x.DefaultQuantityUnit).HasMaxLength(32);
            e.Property(x => x.DefaultPackageQuantity).HasPrecision(20, 6);
            e.Property(x => x.DefaultPackageUnit).HasMaxLength(32);
            e.Property(x => x.ImageReference).HasMaxLength(1000);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceCategory>().WithMany().HasForeignKey(x => x.DefaultCategoryId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Aliases).WithOne(x => x.Product).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Barcodes).WithOne(x => x.Product).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProductAlias>(e =>
        {
            e.HasIndex(x => new { x.ProductId, x.NormalizedAlias });
            e.Property(x => x.Alias).HasMaxLength(500);
            e.Property(x => x.NormalizedAlias).HasMaxLength(500);
            e.Property(x => x.AliasType).HasMaxLength(32);
            e.HasOne<Merchant>().WithMany().HasForeignKey(x => x.MerchantId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ProductBarcode>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(64);
            e.Property(x => x.Standard).HasMaxLength(16);
        });

        b.Entity<PurchasePaymentLink>(e =>
        {
            e.HasIndex(x => x.PurchaseId);
            e.HasIndex(x => x.TransactionId);
            e.HasIndex(x => new { x.PurchaseId, x.TransactionId });
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.LinkSource).HasMaxLength(32);
            e.Property(x => x.Confidence).HasPrecision(5, 4);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FinanceTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PurchaseDocument>(e =>
        {
            e.HasIndex(x => x.PurchaseId);
            e.HasIndex(x => x.Sha256);
            e.Property(x => x.DocumentType).HasMaxLength(32);
            e.Property(x => x.OriginalFileName).HasMaxLength(500);
            e.Property(x => x.MediaType).HasMaxLength(150);
            e.Property(x => x.StoragePath).HasMaxLength(1000);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.PerceptualHash).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasMany(x => x.ExtractionRuns).WithOne(x => x.PurchaseDocument).HasForeignKey(x => x.PurchaseDocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PurchaseExtractionRun>(e =>
        {
            e.HasIndex(x => new { x.PurchaseDocumentId, x.CreatedAt });
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.ProviderVersion).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.ErrorCode).HasMaxLength(80);
            e.Property(x => x.ErrorMessageSafe).HasMaxLength(500);
            e.Property(x => x.RawResultJson).HasColumnType("text");
            e.Property(x => x.NormalizedResultJson).HasColumnType("text");
        });

        b.Entity<PurchaseDifferenceAcceptance>(e =>
        {
            e.HasIndex(x => new { x.PurchaseId, x.Kind }).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Reason).HasMaxLength(32);
            e.Property(x => x.Note).HasMaxLength(500);
        });

        b.Entity<PurchaseItemReturn>(e =>
        {
            e.HasIndex(x => x.PurchaseItemId);
            e.HasIndex(x => x.RefundTransactionId);
            e.Property(x => x.Quantity).HasPrecision(20, 6);
            e.Property(x => x.Amount).HasPrecision(20, 8);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasOne<FinanceTransaction>().WithMany().HasForeignKey(x => x.RefundTransactionId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<FinanceTag>(e =>
        {
            e.HasIndex(x => new { x.FullWorthSpaceId, x.NormalizedName }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.NormalizedName).HasMaxLength(100);
            e.HasOne<FullWorthSpace>().WithMany().HasForeignKey(x => x.FullWorthSpaceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PurchaseTagLink>(e =>
        {
            e.HasIndex(x => new { x.PurchaseId, x.TagId }).IsUnique();
            e.HasIndex(x => x.TagId);
            e.HasOne(x => x.Tag).WithMany(x => x.PurchaseLinks).HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        TaxModelConfiguration.Configure(b);

        // The fast unit-style tests run this model on in-memory SQLite, which cannot order or compare
        // DateTimeOffset columns (many queries order by CreatedAt/UpdatedAt/StartedAt). Store timestamps
        // as sortable binary values under SQLite so those queries translate; PostgreSQL keeps its native
        // timestamptz mapping. Matched by provider name to avoid a SQLite package dependency in production.
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var dateTimeOffsetConverter = new DateTimeOffsetToBinaryConverter();
            foreach (var property in b.Model.GetEntityTypes()
                         .SelectMany(entity => entity.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
                property.SetValueConverter(dateTimeOffsetConverter);
        }
    }
}
