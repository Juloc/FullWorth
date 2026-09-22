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
/// Ein Kauf, eine Abgleichsrechnung (#177).
///
/// Es gab zwei: die Artikelwerkstatt und das Bestaetigen rechneten ueber
/// <c>PurchaseArticleCalculator</c>, <c>GET /api/purchases/{id}/reconciliation</c> dagegen ueber einen
/// eigenen Store. Der war stehengeblieben - er las die Zahlung noch aus der alten Einzelspalte
/// <c>Purchases.TransactionId</c>, kannte die Rabattzeilen nicht und rechnete Trinkgeld, Versand und
/// Gebuehr nicht in die Belegformel. Ein Kauf, der in der Werkstatt bezahlt und stimmig war, stand auf
/// der Kaufseite als "nicht verknuepft" - und damit als unauffaellig.
///
/// Der erste Test ist der, der das gefunden haette: dieselben Daten, zwei Leser, dieselben Zahlen.
/// </summary>
public sealed class PurchaseReconciliationAgreementTests
{
    [Fact]
    public async Task Both_readers_report_the_same_numbers_for_a_purchase_paid_through_payment_links()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = new World();
        await SeedAsync(factory, world, purchaseTotal: 24m, itemTotal: 20m, tip: 4m);
        using var client = factory.CreateClient();

        using (var payment = Request(HttpMethod.Post, $"/api/purchases/{world.Purchase:D}/payments?fullWorthSpaceId={world.Space:D}", world.User,
            new { transactionId = world.Transaction, amount = 24m, currency = "EUR", linkSource = "manual", confidence = (decimal?)null }))
        using (var response = await client.SendAsync(payment))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var direct = await ReadAsync(client, world, $"/api/purchases/{world.Purchase:D}/reconciliation");
        var workspace = (await ReadAsync(client, world, $"/api/purchases/{world.Purchase:D}/workspace")).GetProperty("reconciliation");

        // Die Zahlung ist da - und zwar auf BEIDEN Wegen. Vorher meldete der direkte Weg hier nichts,
        // weil er nur Purchases.TransactionId kannte.
        Assert.Equal(24m, direct.GetProperty("linkedPaymentTotal").GetDecimal());

        foreach (var field in new[]
                 {
                     "purchaseTotal", "itemTotal", "itemDifference", "linkedPaymentTotal", "paymentDifference",
                     "additionalChargeTotal", "totalDiscount", "depositTotal", "roundingAmount"
                 })
            Assert.Equal(workspace.GetProperty(field).GetDecimal(), direct.GetProperty(field).GetDecimal());

        foreach (var flag in new[] { "itemsReconciled", "formulaReconciled", "paymentsReconciled", "fullyReconciled" })
            Assert.Equal(workspace.GetProperty(flag).GetBoolean(), direct.GetProperty(flag).GetBoolean());
    }

    [Fact]
    public async Task A_remaining_item_difference_blocks_confirming_until_it_is_accepted()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = new World();
        await SeedAsync(factory, world, purchaseTotal: 10m, itemTotal: 8m, tip: null);
        using var client = factory.CreateClient();

        using (var request = Request(HttpMethod.Post, Confirm(world), world.User, new { createSafeAllocations = true, allowUnlinked = true }))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("item_difference", json.RootElement.GetProperty("detail").GetProperty("conflict").GetString());
        }

        using (var accept = Request(HttpMethod.Post,
            $"/api/purchases/{world.Purchase:D}/reconciliation/accept-difference?fullWorthSpaceId={world.Space:D}", world.User,
            new { kind = "items", reason = "other", note = (string?)null }))
        using (var response = await client.SendAsync(accept))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using (var request = Request(HttpMethod.Post, Confirm(world), world.User, new { createSafeAllocations = true, allowUnlinked = true }))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == world.Purchase);
            Assert.Equal("confirmed", purchase.Status);
            // Ohne das bleibt der Kauf fuer PurchaseNotificationWorker ewig "zu pruefen".
            Assert.Equal("confirmed", purchase.ReviewState);
        });
    }

    [Fact]
    public async Task Rewriting_the_items_takes_a_previously_accepted_difference_with_it()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = new World();
        await SeedAsync(factory, world, purchaseTotal: 10m, itemTotal: 8m, tip: null);
        using var client = factory.CreateClient();

        using (var accept = Request(HttpMethod.Post,
            $"/api/purchases/{world.Purchase:D}/reconciliation/accept-difference?fullWorthSpaceId={world.Space:D}", world.User,
            new { kind = "items", reason = "other", note = (string?)null }))
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(accept)).StatusCode);

        using (var rewrite = Request(HttpMethod.Put, $"/api/purchases/{world.Purchase:D}/items?fullWorthSpaceId={world.Space:D}", world.User,
            new[] { new { categoryId = (Guid?)null, name = "Anderer Artikel", brand = (string?)null, sku = (string?)null, asin = (string?)null, quantity = 1m, unitPrice = (decimal?)8m, totalPrice = 8m, currency = "EUR", notes = (string?)null } }))
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(rewrite)).StatusCode);

        var state = await ReadAsync(client, world, $"/api/purchases/{world.Purchase:D}/reconciliation");
        Assert.Empty(state.GetProperty("acceptedDifferences").EnumerateObject());

        using var again = Request(HttpMethod.Post, Confirm(world), world.User, new { createSafeAllocations = true, allowUnlinked = true });
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(again)).StatusCode);
    }

    private static string Confirm(World world) =>
        $"/api/purchases/{world.Purchase:D}/confirm?fullWorthSpaceId={world.Space:D}";

    private static async Task<JsonElement> ReadAsync(HttpClient client, World world, string path)
    {
        using var request = Request(HttpMethod.Get, $"{path}?fullWorthSpaceId={world.Space:D}", world.User);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed class World
    {
        public Guid User { get; } = Guid.NewGuid();
        public Guid Space { get; } = Guid.NewGuid();
        public Guid Purchase { get; } = Guid.NewGuid();
        public Guid Transaction { get; } = Guid.NewGuid();
    }

    private static async Task SeedAsync(
        BackendWebApplicationFactory factory, World world, decimal purchaseTotal, decimal itemTotal, decimal? tip)
    {
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.User, EmailNormalized = $"{world.User:N}@EXAMPLE.COM",
                DisplayName = "Reconciliation owner", IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Reconciliation", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space, UserId = world.User, Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connection, FullWorthSpaceId = world.Space, Provider = "test", InstitutionName = "Test",
                Country = "DE", ProviderSessionId = $"rec-{connection:N}", Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account, FullWorthSpaceId = world.Space, BankConnectionId = connection, Provider = "test",
                IdentificationHash = $"rec-{account:N}", ProviderAccountId = $"provider-{account:N}",
                InstitutionName = "Test", DisplayName = "Test", Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = world.User, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = world.Transaction, AccountId = account, ExternalKey = $"rec-{world.Transaction:N}",
                Amount = -purchaseTotal, Currency = "EUR", BookingDate = new DateOnly(2026, 8, 30), RawJson = "{}"
            });
            db.Purchases.Add(new Purchase
            {
                Id = world.Purchase, FullWorthSpaceId = world.Space, Source = "receipt", Merchant = "REWE",
                PurchaseDate = new DateOnly(2026, 8, 30), TotalAmount = purchaseTotal, Currency = "EUR",
                TipAmount = tip, Status = "review", ReviewState = "needs_review", CreatedByUserId = world.User
            });
            db.PurchaseItems.Add(new PurchaseItem
            {
                PurchaseId = world.Purchase, Name = "Kaffee", Quantity = 1m,
                UnitPrice = itemTotal, TotalPrice = itemTotal, Currency = "EUR"
            });
            await db.SaveChangesAsync();
            await CapabilityTestSeeding.GrantEditorAsync(db, world.Space, world.User);
        });
    }
}
