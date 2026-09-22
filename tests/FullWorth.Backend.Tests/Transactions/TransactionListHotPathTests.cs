using System.Data;
using System.Net;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// Was eine Seite der Buchungsliste wirklich kostet (#161, Teil K und L).
///
/// Gemessen an 200 000 Buchungen, 4 000 Kaeufen und 500 Haendlern mit je drei Aliassen:
///
///     Seite ohne Projektion, alle Konten       65 ms
///     dieselbe Seite mit der Projektion       781 ms
///     die zwei Haendlerabfragen danach         1,2 ms
///
/// Das Issue nennt die Haendlerabfragen als das, was aus dem Hot Path muss. Die Messung sagt etwas
/// anderes: sie kosten 1,2 ms, und daneben standen 716 ms Projektion und 65 ms fehlender Index. Diese
/// Tests halten die zwei Ursachen fest, die wirklich zaehlten - und die eine Regel, die beim
/// Umstellen nicht verlorengehen darf.
/// </summary>
public sealed class TransactionListHotPathTests
{
    /// <summary>
    /// Die Standardsicht zeigt ALLE Konten. Der zusammengesetzte Index ueber (Konto, Sortierschluessel)
    /// kann dafuer nichts tun - jede Seite war ein vollstaendiger Durchlauf mit anschliessender
    /// Sortierung, und der Cursor half nicht: er kann nur so schnell sein wie die Sortierung, an der
    /// er haengt.
    /// </summary>
    [Fact]
    public async Task The_all_accounts_timeline_has_an_index_of_its_own()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var definitions = await IndexDefinitionsAsync(db, "Transactions");

        Assert.Contains(definitions, definition =>
            definition.Contains("\"TimelineSortKey\" DESC", StringComparison.Ordinal) &&
            !definition.Contains("\"AccountId\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// Ein Kauf kann an derselben Buchung direkt UND ueber eine Zahlungsverknuepfung haengen. Genau das
    /// war die Aufgabe des alten <c>ODER</c> in der Projektion - und genau die geht verloren, wenn man
    /// die beiden Haelften trennt und das Ergebnis nur addiert. Er zaehlt einmal.
    /// </summary>
    [Fact]
    public async Task A_purchase_linked_twice_to_the_same_booking_counts_once()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, linkTwice: true);
        using var client = factory.CreateClient();

        var item = await SingleItemAsync(client, world);

        Assert.Equal(1, item.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(2, item.GetProperty("purchaseItemCount").GetInt32());
    }

    /// <summary>Und der gewoehnliche Fall bleibt, was er war: ein Kauf, seine Artikel.</summary>
    [Fact]
    public async Task A_purchase_linked_only_through_a_payment_link_is_counted()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, linkTwice: false, direct: false);
        using var client = factory.CreateClient();

        var item = await SingleItemAsync(client, world);

        Assert.Equal(1, item.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(2, item.GetProperty("purchaseItemCount").GetInt32());
    }

    /// <summary>
    /// Ein privater Kauf eines anderen Mitglieds zaehlt nicht mit. Die Sichtbarkeitsregel stand in der
    /// alten Unterabfrage und muss die Umstellung ueberleben - sonst verraet schon die Zahl neben der
    /// Buchung, dass es dort etwas gibt.
    /// </summary>
    [Fact]
    public async Task A_private_purchase_of_another_member_is_not_counted()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, linkTwice: false, visibility: "private", otherOwner: true);
        using var client = factory.CreateClient();

        var item = await SingleItemAsync(client, world);

        Assert.Equal(0, item.GetProperty("purchaseCount").GetInt32());
        Assert.Equal(0, item.GetProperty("purchaseItemCount").GetInt32());
    }

    private static async Task<JsonElement> SingleItemAsync(HttpClient client, World world)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/transactions?fullWorthSpaceId={world.Space:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", world.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        return items[0].Clone();
    }

    private static async Task<HashSet<string>> IndexDefinitionsAsync(FullWorthDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = '{table}';";
            await using var reader = await command.ExecuteReaderAsync();
            var definitions = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync()) definitions.Add(reader.GetString(0));
            return definitions;
        }
        finally { if (closeWhenDone) await connection.CloseAsync(); }
    }

    private sealed record World(Guid Space, Guid Owner, Guid Transaction);

    private static async Task<World> SeedAsync(
        BackendWebApplicationFactory factory,
        bool linkTwice,
        bool direct = true,
        string visibility = "space",
        bool otherOwner = false)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.Users.Add(new FullWorthUser
            {
                Id = stranger,
                EmailNormalized = $"{stranger:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Anderes Mitglied",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Hotpath", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space,
                UserId = world.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space,
                UserId = stranger,
                Role = FullWorthSpaceRoles.Member
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connectionId,
                FullWorthSpaceId = world.Space,
                Provider = "manual",
                InstitutionName = "Testbank",
                Status = "authorized"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = world.Space,
                BankConnectionId = connectionId,
                IdentificationHash = $"h-{accountId:N}",
                ProviderAccountId = $"a-{accountId:N}",
                InstitutionName = "Testbank",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = world.Owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = world.Transaction,
                AccountId = accountId,
                ExternalKey = "hp-1",
                Amount = -42.19m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 9, 1),
                Counterparty = "REWE",
                RawJson = "{}"
            });
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = world.Space,
                CreatedByUserId = otherOwner ? stranger : world.Owner,
                TransactionId = direct || linkTwice ? world.Transaction : null,
                Merchant = "REWE",
                TotalAmount = 42.19m,
                Currency = "EUR",
                Visibility = visibility
            });
            for (var index = 0; index < 2; index++)
                db.PurchaseItems.Add(new PurchaseItem
                {
                    PurchaseId = purchaseId,
                    Name = $"Artikel {index}",
                    RawName = $"Artikel {index}",
                    Quantity = 1,
                    TotalPrice = 1m,
                    Currency = "EUR"
                });
            if (linkTwice || !direct)
                db.PurchasePaymentLinks.Add(new PurchasePaymentLink
                {
                    FullWorthSpaceId = world.Space,
                    PurchaseId = purchaseId,
                    TransactionId = world.Transaction,
                    Amount = 42.19m,
                    Currency = "EUR"
                });
            await db.SaveChangesAsync();
        });
        return world;
    }
}
