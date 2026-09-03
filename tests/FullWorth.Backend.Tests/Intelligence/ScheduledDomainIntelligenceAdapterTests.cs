using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class ScheduledDomainIntelligenceAdapterTests
{
    [Fact]
    public async Task Product_adapter_creates_reviewable_proposal_without_changing_purchase_item()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = fixture.AddPurchase("REWE", new DateOnly(2026, 9, 1));
        var item = new PurchaseItem
        {
            Purchase = purchase,
            PurchaseId = purchase.Id,
            RawName = "COCA COLA ZERO 1,5L",
            Name = "Coca Cola Zero 1,5L",
            Brand = "Coca-Cola",
            Barcode = "5449000054539",
            Currency = "EUR",
            TotalPrice = 1.49m
        };
        purchase.Items.Add(item);
        fixture.FinanceDb.Purchases.Add(purchase);
        await fixture.FinanceDb.SaveChangesAsync();

        var settings = fixture.Settings(Product: true);
        fixture.Provider.OutputFactory = request =>
            request.SystemInstruction.Contains("product normalization", StringComparison.OrdinalIgnoreCase)
                ? $$"""{"suggestions":[{"itemId":"{{item.Id:N}}","canonicalName":"Coca-Cola Zero 1.5L","categoryKey":"food.groceries","confidenceBand":"high","evidenceSummary":"Barcode and article name match a grocery product."}]}"""
                : "{\"suggestions\":[]}";

        await fixture.Adapter.ProcessAsync(fixture.Job, fixture.Space.Id, settings, fixture.Credential, CancellationToken.None);

        var suggestion = await fixture.IntelligenceDb.IntelligenceSuggestions.SingleAsync();
        Assert.Equal("product-normalization", suggestion.Type);
        Assert.Equal("purchase-item", suggestion.SubjectType);
        Assert.Equal(item.Id.ToString("N"), suggestion.SubjectId);
        Assert.Equal(IntelligenceSuggestionStatuses.Pending, suggestion.Status);

        var stored = await fixture.FinanceDb.PurchaseItems.AsNoTracking().SingleAsync(x => x.Id == item.Id);
        Assert.Null(stored.ProductId);
        Assert.Null(stored.CategoryId);
        Assert.Equal("Coca Cola Zero 1,5L", stored.Name);
        Assert.False(stored.IsManuallyCorrected);
    }

    [Fact]
    public async Task Receipt_adapter_creates_follow_up_without_changing_document()
    {
        await using var fixture = await Fixture.CreateAsync();
        var purchase = fixture.AddPurchase("LIDL", new DateOnly(2026, 9, 1));
        var document = new PurchaseDocument
        {
            Purchase = purchase,
            PurchaseId = purchase.Id,
            DocumentType = "receipt",
            OriginalFileName = "bon.pdf",
            MediaType = "application/pdf",
            StoragePath = "test/bon.pdf",
            Sha256 = new string('a', 64),
            PageCount = 2,
            Status = "uploaded"
        };
        document.ExtractionRuns.Add(new PurchaseExtractionRun
        {
            PurchaseDocument = document,
            PurchaseDocumentId = document.Id,
            Provider = "local-ocr",
            Status = "failed",
            ErrorCode = "low_quality",
            CompletedAt = DateTimeOffset.UtcNow
        });
        purchase.Documents.Add(document);
        fixture.FinanceDb.Purchases.Add(purchase);
        await fixture.FinanceDb.SaveChangesAsync();

        var settings = fixture.Settings(Receipt: true);
        fixture.Provider.OutputFactory = request =>
            request.SystemInstruction.Contains("receipt follow-up", StringComparison.OrdinalIgnoreCase)
                ? $$"""{"suggestions":[{"documentId":"{{document.Id:N}}","action":"manual_review","confidenceBand":"high","evidenceSummary":"The latest extraction failed for a multi-page receipt."}]}"""
                : "{\"suggestions\":[]}";

        await fixture.Adapter.ProcessAsync(fixture.Job, fixture.Space.Id, settings, fixture.Credential, CancellationToken.None);

        var suggestion = await fixture.IntelligenceDb.IntelligenceSuggestions.SingleAsync();
        Assert.Equal("receipt-follow-up", suggestion.Type);
        Assert.Equal("receipt-document", suggestion.SubjectType);
        Assert.Contains("manual_review", suggestion.ProposedPayloadJson, StringComparison.Ordinal);

        var stored = await fixture.FinanceDb.PurchaseDocuments.AsNoTracking().SingleAsync(x => x.Id == document.Id);
        Assert.Equal("uploaded", stored.Status);
        var extraction = await fixture.FinanceDb.PurchaseExtractionRuns.AsNoTracking().SingleAsync();
        Assert.Equal("failed", extraction.Status);
        Assert.Equal("low_quality", extraction.ErrorCode);
    }

    [Fact]
    public async Task Contract_adapter_enriches_deterministic_candidate_without_creating_contract()
    {
        await using var fixture = await Fixture.CreateAsync();
        var account = new FinanceAccount
        {
            FullWorthSpaceId = fixture.Space.Id,
            Provider = "manual",
            IdentificationHash = "contract-test-account",
            ProviderAccountId = "contract-test-account",
            InstitutionName = "Test",
            DisplayName = "Testkonto",
            Currency = "EUR"
        };
        fixture.FinanceDb.Accounts.Add(account);
        for (var month = 6; month <= 9; month++)
        {
            fixture.FinanceDb.Transactions.Add(new FinanceTransaction
            {
                AccountId = account.Id,
                ExternalKey = $"netflix-{month}",
                BookingDate = new DateOnly(2026, month, 1),
                Amount = -13.99m,
                Currency = "EUR",
                Counterparty = "NETFLIX.COM",
                NormalizedCounterparty = "NETFLIX",
                CategorizationSource = "none",
                FirstSeenAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await fixture.FinanceDb.SaveChangesAsync();

        var settings = fixture.Settings(Contract: true);
        fixture.Provider.OutputFactory = request =>
            request.SystemInstruction.Contains("recurring-contract", StringComparison.OrdinalIgnoreCase)
                ? """{"suggestions":[{"merchant":"NETFLIX","currency":"EUR","providerName":"Netflix","contractKind":"streaming","categoryKey":"subscriptions.streaming","confidenceBand":"high","evidenceSummary":"Stable monthly recurring payment."}]}"""
                : "{\"suggestions\":[]}";

        await fixture.Adapter.ProcessAsync(fixture.Job, fixture.Space.Id, settings, fixture.Credential, CancellationToken.None);

        var suggestion = await fixture.IntelligenceDb.IntelligenceSuggestions.SingleAsync();
        Assert.Equal("contract-enrichment", suggestion.Type);
        Assert.Equal("contract-candidate", suggestion.SubjectType);
        Assert.Contains("Netflix", suggestion.ProposedPayloadJson, StringComparison.Ordinal);
        Assert.Empty(await fixture.FinanceDb.Contracts.AsNoTracking().ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection intelligenceConnection;
        private readonly SqliteConnection financeConnection;

        private Fixture(
            SqliteConnection intelligenceConnection,
            SqliteConnection financeConnection,
            IntelligenceDbContext intelligenceDb,
            FullWorthDbContext financeDb,
            FakeProvider provider,
            IntelligenceStore store,
            AiCredential credential,
            ScheduledDomainIntelligenceAdapters adapter,
            FullWorthSpace space,
            IntelligenceJob job)
        {
            this.intelligenceConnection = intelligenceConnection;
            this.financeConnection = financeConnection;
            IntelligenceDb = intelligenceDb;
            FinanceDb = financeDb;
            Provider = provider;
            Store = store;
            Credential = credential;
            Adapter = adapter;
            Space = space;
            Job = job;
        }

        public IntelligenceDbContext IntelligenceDb { get; }
        public FullWorthDbContext FinanceDb { get; }
        public FakeProvider Provider { get; }
        public IntelligenceStore Store { get; }
        public AiCredential Credential { get; }
        public ScheduledDomainIntelligenceAdapters Adapter { get; }
        public FullWorthSpace Space { get; }
        public IntelligenceJob Job { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var intelligenceConnection = new SqliteConnection("Data Source=:memory:");
            var financeConnection = new SqliteConnection("Data Source=:memory:");
            await intelligenceConnection.OpenAsync();
            await financeConnection.OpenAsync();

            var intelligenceDb = new IntelligenceDbContext(
                new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(intelligenceConnection).Options);
            var financeDb = new FullWorthDbContext(
                new DbContextOptionsBuilder<FullWorthDbContext>().UseSqlite(financeConnection).Options);
            await intelligenceDb.Database.EnsureCreatedAsync();
            await financeDb.Database.EnsureCreatedAsync();

            var space = new FullWorthSpace { Name = "Intelligence adapters", BaseCurrency = "EUR" };
            financeDb.FullWorthSpaces.Add(space);
            financeDb.Categories.AddRange(
                new FinanceCategory
                {
                    FullWorthSpaceId = space.Id,
                    Key = "food.groceries",
                    Name = "Lebensmittel",
                    IsSystem = true
                },
                new FinanceCategory
                {
                    FullWorthSpaceId = space.Id,
                    Key = "subscriptions.streaming",
                    Name = "Streaming",
                    IsSystem = true
                });
            await financeDb.SaveChangesAsync();

            var provider = new FakeProvider();
            var registry = new IntelligenceProviderRegistry([provider]);
            var store = new IntelligenceStore(intelligenceDb, FieldCipher.Null, registry);
            var credentialView = await store.CreateCredentialAsync(null, "fake", "scheduled-adapters", "test-secret-value", CancellationToken.None);
            var credential = await intelligenceDb.AiCredentials.SingleAsync(x => x.Id == credentialView.Id);
            var adapter = new ScheduledDomainIntelligenceAdapters(
                intelligenceDb,
                financeDb,
                store,
                registry,
                new AiBudgetGuard(intelligenceDb),
                new AiCostEstimator(new ConfigurationBuilder().Build()));
            var job = new IntelligenceJob
            {
                Type = ScheduledIntelligenceJobTypes.DailyIncremental,
                ScopeKey = "instance",
                IdempotencyKey = $"adapter-test:{Guid.NewGuid():N}",
                Status = IntelligenceJobStatuses.Running,
                StartedAt = DateTimeOffset.UtcNow
            };
            intelligenceDb.IntelligenceJobs.Add(job);
            // AiBudgetGuard re-reads the singleton AiInstanceSettings from the database; without a
            // persisted, enabled row every adapter run is gated off as "ai_disabled".
            intelligenceDb.AiInstanceSettings.Add(new AiInstanceSettings
            {
                ScopeKey = AiInstanceSettings.InstanceScopeKey,
                Enabled = true
            });
            await intelligenceDb.SaveChangesAsync();

            return new Fixture(intelligenceConnection, financeConnection, intelligenceDb, financeDb, provider, store,
                credential, adapter, space, job);
        }

        public AiInstanceSettings Settings(bool Product = false, bool Receipt = false, bool Contract = false) => new()
        {
            Enabled = true,
            Provider = "fake",
            CredentialId = Credential.Id,
            DefaultTextModel = "fake-model",
            DefaultVisionModel = "fake-model",
            ProductAiEnabled = Product,
            ReceiptAiEnabled = Receipt,
            ContractAiEnabled = Contract
        };

        public Purchase AddPurchase(string merchant, DateOnly date) => new()
        {
            FullWorthSpaceId = Space.Id,
            Source = "receipt",
            Merchant = merchant,
            PurchaseDate = date,
            TotalAmount = 10m,
            Currency = "EUR"
        };

        public async ValueTask DisposeAsync()
        {
            await IntelligenceDb.DisposeAsync();
            await FinanceDb.DisposeAsync();
            await intelligenceConnection.DisposeAsync();
            await financeConnection.DisposeAsync();
        }
    }

    private sealed class FakeProvider : IIntelligenceProvider
    {
        public Func<IntelligenceProviderRequest, string> OutputFactory { get; set; } = _ => "{\"suggestions\":[]}";
        public int ExecuteCount { get; private set; }

        public IntelligenceProviderDescriptor Descriptor { get; } = new(
            "fake",
            IntelligenceProviderCapabilities.TextClassification | IntelligenceProviderCapabilities.StructuredExtraction,
            1024 * 1024,
            ReportsUsage: true);

        public Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken cancellationToken) =>
            Task.FromResult(new IntelligenceProviderTestResult(true));

        public Task<IntelligenceProviderResponse> ExecuteAsync(
            IntelligenceProviderRequest request,
            string credential,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            Assert.Equal("test-secret-value", credential);
            Assert.DoesNotContain("iban", request.InputJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("description", request.InputJson, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new IntelligenceProviderResponse(OutputFactory(request), 25, 15, $"fake-{ExecuteCount}"));
        }
    }
}
