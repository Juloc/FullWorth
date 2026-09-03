using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

public sealed class PurchaseCanonicalAllocationFlowTests
{
    [Fact]
    public async Task Confirm_and_workspace_import_share_item_discount_coupon_deposit_and_rounding_breakdown()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var colaId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var itemDiscountId = Guid.NewGuid();
        var basketDiscountId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Canonical allocation",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Canonical allocation", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = $"canonical-{accountId:N}",
                ProviderAccountId = $"manual-{accountId:N}",
                InstitutionName = "Cash",
                DisplayName = "Wallet",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId,
                AccountId = accountId,
                ExternalKey = $"manual:{transactionId:N}",
                Amount = -13.24m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 8, 31),
                Counterparty = "Testmarkt",
                RawJson = "{}"
            });
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = spaceId,
                Merchant = "Testmarkt",
                Source = "receipt",
                PurchaseDate = new DateOnly(2026, 8, 31),
                SubtotalAmount = 17m,
                DiscountAmount = 4m,
                DepositAmount = .25m,
                RoundingAmount = -.01m,
                TotalAmount = 13.24m,
                Currency = "EUR",
                Status = "review",
                ReviewState = "needs_review",
                CreatedByUserId = userId,
                Visibility = "space"
            });
            db.PurchaseItems.AddRange(
                new PurchaseItem
                {
                    Id = colaId,
                    PurchaseId = purchaseId,
                    RawName = "Cola",
                    Name = "Cola",
                    Quantity = 1m,
                    QuantityUnit = "piece",
                    OriginalUnitPrice = 12m,
                    UnitPrice = 10m,
                    TotalPrice = 10m,
                    DiscountAmount = 2m,
                    DiscountLabel = "Artikelaktion",
                    DepositAmount = .25m,
                    Currency = "EUR",
                    LineType = "product",
                    SortOrder = 0
                },
                new PurchaseItem
                {
                    Id = foodId,
                    PurchaseId = purchaseId,
                    RawName = "Essen",
                    Name = "Essen",
                    Quantity = 1m,
                    QuantityUnit = "piece",
                    UnitPrice = 5m,
                    TotalPrice = 5m,
                    Currency = "EUR",
                    LineType = "product",
                    SortOrder = 1
                });
            db.Set<PurchaseDiscount>().AddRange(
                new PurchaseDiscount
                {
                    Id = itemDiscountId,
                    PurchaseId = purchaseId,
                    PurchaseItemId = colaId,
                    Type = "price_reduction",
                    Label = "Artikelaktion",
                    Amount = 2m,
                    Source = "manual"
                },
                new PurchaseDiscount
                {
                    Id = basketDiscountId,
                    PurchaseId = purchaseId,
                    Type = "coupon",
                    Label = "5 € Coupon anteilig",
                    Amount = 2m,
                    Source = "manual"
                });
            db.PurchasePaymentLinks.Add(new PurchasePaymentLink
            {
                FullWorthSpaceId = spaceId,
                PurchaseId = purchaseId,
                TransactionId = transactionId,
                Amount = 13.24m,
                Currency = "EUR",
                LinkSource = "manual",
                CreatedByUserId = userId
            });
            await db.SaveChangesAsync();

            // Purchase mutation routes require the purchases.manage capability; grant the acting member the
            // editor template (account ownership is still enforced separately).
            await CapabilityTestSeeding.GrantEditorAsync(db, spaceId, userId);
        });

        using var client = factory.CreateClient();
        using var confirm = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/purchases/{purchaseId:D}/confirm?fullWorthSpaceId={spaceId:D}",
            userId,
            new { createSafeAllocations = true, allowUnlinked = false }));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var confirmed = await SnapshotAsync(factory, transactionId, purchaseId);
        AssertCanonicalBreakdown(confirmed, colaId, foodId, basketDiscountId);

        using var clear = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/transactions/{transactionId:D}/allocations/clear?fullWorthSpaceId={spaceId:D}",
            userId,
            new { }));
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

        using var import = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/transactions/{transactionId:D}/allocations/from-purchase/{purchaseId:D}?fullWorthSpaceId={spaceId:D}",
            userId,
            new { mode = "replace", addRemainder = false }));
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var imported = await SnapshotAsync(factory, transactionId, purchaseId);
        AssertCanonicalBreakdown(imported, colaId, foodId, basketDiscountId);
        Assert.Equal(confirmed.Allocations.Select(x => (x.Amount, x.Note)).OrderBy(x => x.Amount).ThenBy(x => x.Note),
            imported.Allocations.Select(x => (x.Amount, x.Note)).OrderBy(x => x.Amount).ThenBy(x => x.Note));
    }

    private static void AssertCanonicalBreakdown(
        AllocationSnapshot snapshot,
        Guid colaId,
        Guid foodId,
        Guid basketDiscountId)
    {
        Assert.Equal(5, snapshot.Allocations.Count);
        Assert.Equal(-10m, snapshot.Allocations.Single(x => x.PurchaseItemId == colaId && x.Note == "Cola").Amount);
        Assert.Equal(-.25m, snapshot.Allocations.Single(x => x.PurchaseItemId == colaId && x.Note!.StartsWith("Pfand", StringComparison.Ordinal)).Amount);
        Assert.Equal(-5m, snapshot.Allocations.Single(x => x.PurchaseItemId == foodId).Amount);
        Assert.Equal(2m, snapshot.Allocations.Single(x => x.Note == "5 € Coupon anteilig").Amount);
        Assert.Equal(.01m, snapshot.Allocations.Single(x => x.Note == "Rundung").Amount);
        Assert.Equal(-13.24m, snapshot.Allocations.Sum(x => x.Amount));

        Assert.Equal(5, snapshot.Provenance.Count);
        Assert.Contains(snapshot.Provenance, x => x.AllocationType == "article" && x.PurchaseDiscountId == null);
        Assert.Contains(snapshot.Provenance, x => x.AllocationType == "deposit" && x.PurchaseDiscountId == null);
        Assert.Contains(snapshot.Provenance, x => x.AllocationType == "discount" && x.PurchaseDiscountId == basketDiscountId);
        Assert.Contains(snapshot.Provenance, x => x.AllocationType == "rounding" && x.PurchaseDiscountId == null);
        Assert.DoesNotContain(snapshot.Provenance, x => x.PurchaseDiscountId != null && x.PurchaseDiscountId != basketDiscountId);
    }

    private static async Task<AllocationSnapshot> SnapshotAsync(
        BackendWebApplicationFactory factory,
        Guid transactionId,
        Guid purchaseId)
    {
        AllocationSnapshot? result = null;
        await factory.SeedAsync(async db =>
        {
            var allocations = await db.TransactionAllocations.AsNoTracking()
                .Where(x => x.TransactionId == transactionId)
                .OrderBy(x => x.CreatedAt)
                .Select(x => new AllocationRow(x.Id, x.PurchaseItemId, x.Amount, x.Note))
                .ToListAsync();
            var provenance = await db.Set<PurchaseAllocationLink>().AsNoTracking()
                .Where(x => x.PurchaseId == purchaseId)
                .OrderBy(x => x.CreatedAt)
                .Select(x => new ProvenanceRow(x.TransactionAllocationId, x.PurchaseDiscountId, x.AllocationType))
                .ToListAsync();
            result = new AllocationSnapshot(allocations, provenance);
        });
        return result!;
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object body)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record AllocationSnapshot(List<AllocationRow> Allocations, List<ProvenanceRow> Provenance);
    private sealed record AllocationRow(Guid Id, Guid? PurchaseItemId, decimal Amount, string? Note);
    private sealed record ProvenanceRow(Guid TransactionAllocationId, Guid? PurchaseDiscountId, string AllocationType);
}
