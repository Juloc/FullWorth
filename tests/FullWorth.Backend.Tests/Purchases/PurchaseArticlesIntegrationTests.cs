using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Regression coverage for the article/document model while the legacy /api/purchases surface remains
/// active for scan/Amazon/UI compatibility. These tests protect the invariants that are easiest to
/// accidentally bypass when both API generations coexist.
/// </summary>
public sealed class PurchaseArticlesIntegrationTests
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00];

    [Fact]
    public async Task ReceiptScan_CreatesDocumentHashAndCreatorMetadata()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user) = await SeedMembersAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ReceiptRequest(space, user));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<PurchaseProbe>();
        Assert.NotNull(body);

        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == body!.Id);
            var document = await db.PurchaseDocuments.AsNoTracking().SingleAsync(x => x.PurchaseId == body.Id);

            Assert.Equal(user, purchase.CreatedByUserId);
            Assert.Equal("space", purchase.Visibility);
            Assert.Equal("needs_review", purchase.ReviewState);
            Assert.Equal("receipt", document.DocumentType);
            Assert.Equal("image/png", document.MediaType);
            Assert.Equal(64, document.Sha256.Length);
            Assert.Equal(PngBytes.Length, document.SizeBytes);
            Assert.False(string.IsNullOrWhiteSpace(document.StoragePath));
        });
    }

    [Fact]
    public async Task ReceiptScan_RejectsExactDuplicateWithinFullWorthSpace()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, user) = await SeedMembersAsync(factory);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(ReceiptRequest(space, user));
        using var duplicate = await client.SendAsync(ReceiptRequest(space, user));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Contains("already stored", await duplicate.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Purchases.CountAsync(x => x.FullWorthSpaceId == space));
            Assert.Equal(1, await db.PurchaseDocuments.CountAsync(x => x.Purchase.FullWorthSpaceId == space));
        });
    }

    [Fact]
    public async Task PrivatePurchase_IsHiddenFromOtherSpaceMemberThroughLegacyRoutes()
    {
        using var factory = new BackendWebApplicationFactory();
        var (space, creator, other) = await SeedTwoMembersAsync(factory);
        var purchaseId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = space,
                Merchant = "Private Shop",
                Source = "manual",
                PurchaseDate = new DateOnly(2026, 8, 30),
                TotalAmount = 42m,
                Currency = "EUR",
                Status = "review",
                ReviewState = "needs_review",
                Visibility = "private",
                CreatedByUserId = creator
            });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var creatorList = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/purchases?fullWorthSpaceId={space:D}", creator));
        using var otherList = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/purchases?fullWorthSpaceId={space:D}", other));
        using var otherDetail = await client.SendAsync(UserRequest(HttpMethod.Get, $"/api/purchases/{purchaseId:D}?fullWorthSpaceId={space:D}", other));

        Assert.Equal(HttpStatusCode.OK, creatorList.StatusCode);
        Assert.Contains(purchaseId.ToString(), await creatorList.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, otherList.StatusCode);
        Assert.DoesNotContain(purchaseId.ToString(), await otherList.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, otherDetail.StatusCode);
    }

    [Fact]
    public async Task LegacyLink_CreatesPaymentLinkButDoesNotBypassReviewConfirmation()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        var space = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        var purchase = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(User(user));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Articles", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(Member(space, user));
            db.BankConnections.Add(new BankConnection
            {
                Id = connection,
                FullWorthSpaceId = space,
                Provider = "test",
                InstitutionName = "Test Bank",
                Country = "DE",
                ProviderSessionId = $"articles-{connection:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = space,
                BankConnectionId = connection,
                Provider = "test",
                IdentificationHash = $"articles-{account:N}",
                ProviderAccountId = $"provider-{account:N}",
                InstitutionName = "Test Bank",
                DisplayName = "Card",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transaction,
                AccountId = account,
                ExternalKey = $"articles-{transaction:N}",
                Amount = -12.34m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 8, 30),
                Counterparty = "Shop",
                RawJson = "{}"
            });
            db.Purchases.Add(new Purchase
            {
                Id = purchase,
                FullWorthSpaceId = space,
                Merchant = "Shop",
                Source = "receipt",
                PurchaseDate = new DateOnly(2026, 8, 30),
                TotalAmount = 12.34m,
                Currency = "EUR",
                Status = "review",
                ReviewState = "needs_review",
                CreatedByUserId = user,
                Visibility = "space"
            });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member owns the account/purchase but resolves to
            // the read-only viewer template, so grant editor to reach the /api/purchases link handler.
            await CapabilityTestSeeding.GrantEditorAsync(db, space, user);
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/purchases/{purchase:D}/link?fullWorthSpaceId={space:D}", user,
            new { transactionId = transaction, confidence = .99m }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var p = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchase);
            var link = await db.PurchasePaymentLinks.AsNoTracking().SingleAsync(x => x.PurchaseId == purchase);
            Assert.Equal(transaction, p.TransactionId);
            Assert.Equal("review", p.Status);
            Assert.Equal("needs_review", p.ReviewState);
            Assert.Equal(transaction, link.TransactionId);
            Assert.Equal(12.34m, link.Amount);
            Assert.Equal(.99m, link.Confidence);
        });
    }

    [Fact]
    public async Task LegacyItemReplace_DetachesArticleAllocationInsteadOfDeletingFinancialSplit()
    {
        using var factory = new BackendWebApplicationFactory();
        var user = Guid.NewGuid();
        var space = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        var transaction = Guid.NewGuid();
        var purchase = Guid.NewGuid();
        var oldItem = Guid.NewGuid();
        var allocation = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(User(user));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Allocations", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(Member(space, user));
            db.BankConnections.Add(new BankConnection { Id = connection, FullWorthSpaceId = space, Provider = "test", InstitutionName = "Test", Country = "DE", ProviderSessionId = $"alloc-{connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = account, FullWorthSpaceId = space, BankConnectionId = connection, Provider = "test", IdentificationHash = $"alloc-{account:N}", ProviderAccountId = $"provider-{account:N}", InstitutionName = "Test", DisplayName = "Test", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction { Id = transaction, AccountId = account, ExternalKey = $"alloc-{transaction:N}", Amount = -10m, Currency = "EUR", BookingDate = new DateOnly(2026, 8, 30), RawJson = "{}" });
            db.Purchases.Add(new Purchase { Id = purchase, FullWorthSpaceId = space, TransactionId = transaction, Merchant = "Shop", Source = "receipt", PurchaseDate = new DateOnly(2026, 8, 30), TotalAmount = 10m, Currency = "EUR", Status = "review", ReviewState = "needs_review", CreatedByUserId = user });
            db.PurchaseItems.Add(new PurchaseItem { Id = oldItem, PurchaseId = purchase, RawName = "Old", Name = "Old", Quantity = 1m, QuantityUnit = "piece", TotalPrice = 10m, Currency = "EUR", SortOrder = 0 });
            db.TransactionAllocations.Add(new TransactionAllocation { Id = allocation, TransactionId = transaction, Amount = -10m, Note = "Old", PurchaseItemId = oldItem });
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member owns the account/purchase but resolves to
            // the read-only viewer template, so grant editor to reach the /api/purchases items handler.
            await CapabilityTestSeeding.GrantEditorAsync(db, space, user);
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/purchases/{purchase:D}/items?fullWorthSpaceId={space:D}", user,
            new[] { new { categoryId = (Guid?)null, name = "New", brand = (string?)null, sku = (string?)null, asin = (string?)null, quantity = 1m, unitPrice = (decimal?)10m, totalPrice = 10m, currency = "EUR", notes = (string?)null } }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var line = await db.TransactionAllocations.AsNoTracking().SingleAsync(x => x.Id == allocation);
            Assert.Null(line.PurchaseItemId);
            Assert.Equal(-10m, line.Amount);
            Assert.False(await db.PurchaseItems.AnyAsync(x => x.Id == oldItem));
            Assert.True(await db.PurchaseItems.AnyAsync(x => x.PurchaseId == purchase && x.Name == "New"));
        });
    }

    private static async Task<(Guid Space, Guid User)> SeedMembersAsync(BackendWebApplicationFactory factory)
    {
        var space = Guid.NewGuid();
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(User(user));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Receipt Documents", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(Member(space, user));
            await db.SaveChangesAsync();

            // Predates the capability layer: the acting member resolves to the read-only viewer template,
            // so grant editor (carrying purchases.manage) to reach the receipt-scan write handler.
            await CapabilityTestSeeding.GrantEditorAsync(db, space, user);
        });
        return (space, user);
    }

    private static async Task<(Guid Space, Guid Creator, Guid Other)> SeedTwoMembersAsync(BackendWebApplicationFactory factory)
    {
        var space = Guid.NewGuid();
        var creator = Guid.NewGuid();
        var other = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(User(creator), User(other));
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Privacy", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(Member(space, creator), Member(space, other));
            await db.SaveChangesAsync();
        });
        return (space, creator, other);
    }

    private static FullWorthUser User(Guid id) => new()
    {
        Id = id,
        EmailNormalized = $"{id:N}@EXAMPLE.COM".ToUpperInvariant(),
        DisplayName = $"Articles {id:N}",
        IsActive = true
    };

    private static FullWorthSpaceMember Member(Guid space, Guid user) => new()
    {
        FullWorthSpaceId = space,
        UserId = user,
        Role = FullWorthSpaceRoles.Member
    };

    private static HttpRequestMessage ReceiptRequest(Guid fullWorthSpaceId, Guid userId)
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("EUR"), "currency");
        multipart.Add(new ByteArrayContent(PngBytes), "receipt", "receipt.png");
        return UserRequest(HttpMethod.Post, $"/api/purchases/receipt-scan?fullWorthSpaceId={fullWorthSpaceId:D}", userId, multipart);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is HttpContent content) request.Content = content;
        else if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record PurchaseProbe(Guid Id);
}
