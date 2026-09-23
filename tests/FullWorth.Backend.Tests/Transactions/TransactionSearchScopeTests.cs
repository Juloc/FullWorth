using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// Die Suche bleibt im Raum des Suchenden - auch der Umweg ueber Kaeufe (#161, Teil J).
///
/// Ein Suchbegriff trifft eine Buchung auf zwei Wegen: ueber ihren eigenen Wortindex, und ueber einen
/// Kauf, der auf sie zeigt. Der zweite Weg wird VORAB zu Buchungskennungen aufgeloest, damit der
/// Wortindex nicht wertlos wird - und genau dabei laesst sich der Raum leicht vergessen.
///
/// Gefaehrlich waere das nicht: die Haupttimeline enthaelt ohnehin nur zugaengliche Buchungen, eine
/// fremde Kennung liefe dort ins Leere. Aber die Vorabaufloesung hat eine Obergrenze, und ohne
/// Raumfilter fuellen fremde Kaeufe sie auf - dann fehlen dem Benutzer eigene Treffer, ohne dass
/// irgendwo ein Fehler auftaucht.
/// </summary>
public sealed class TransactionSearchScopeTests
{
    [Fact]
    public async Task A_purchase_with_the_same_name_in_another_space_changes_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        var mine = new Space("Mein Raum");
        var other = new Space("Fremder Raum");
        await SeedAsync(factory, mine);
        await SeedAsync(factory, other);
        using var client = factory.CreateClient();

        var hits = await SearchAsync(client, mine, "Zauberstab");

        Assert.Equal([mine.Transaction], hits);
    }

    [Fact]
    public async Task The_same_search_run_by_the_other_owner_finds_only_their_own()
    {
        using var factory = new BackendWebApplicationFactory();
        var mine = new Space("Mein Raum");
        var other = new Space("Fremder Raum");
        await SeedAsync(factory, mine);
        await SeedAsync(factory, other);
        using var client = factory.CreateClient();

        Assert.Equal([other.Transaction], await SearchAsync(client, other, "Zauberstab"));
    }

    private static async Task<Guid[]> SearchAsync(HttpClient client, Space space, string term)
    {
        var path = $"/api/transactions?fullWorthSpaceId={space.Id:D}&query={Uri.EscapeDataString(term)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", space.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();
    }

    private sealed class Space(string name)
    {
        public string Name { get; } = name;
        public Guid Id { get; } = Guid.NewGuid();
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Connection { get; } = Guid.NewGuid();
        public Guid Account { get; } = Guid.NewGuid();
        public Guid Transaction { get; } = Guid.NewGuid();
        public Guid Purchase { get; } = Guid.NewGuid();
    }

    private static async Task SeedAsync(BackendWebApplicationFactory factory, Space space)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = space.Owner,
                EmailNormalized = $"{space.Owner:N}@EXAMPLE.COM",
                DisplayName = space.Name,
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space.Id, Name = space.Name, BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space.Id, UserId = space.Owner, Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = space.Connection, FullWorthSpaceId = space.Id, Provider = "manual",
                InstitutionName = "Testbank", Country = "DE",
                ProviderSessionId = $"scope-{space.Connection:N}", Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = space.Account, FullWorthSpaceId = space.Id, BankConnectionId = space.Connection,
                Provider = "manual", IdentificationHash = $"scope-{space.Account:N}",
                ProviderAccountId = $"provider-{space.Account:N}", InstitutionName = "Testbank",
                DisplayName = "Girokonto", Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = space.Account, UserId = space.Owner, OwnershipType = AccountOwnershipTypes.Owner
            });

            // Der Suchbegriff steht NUR im Kauf, nicht in der Buchung - sonst traefe der Wortindex
            // direkt und der Umweg, um den es hier geht, waere gar nicht beteiligt.
            db.Transactions.Add(new FinanceTransaction
            {
                Id = space.Transaction, AccountId = space.Account, ExternalKey = $"scope-{space.Transaction:N}",
                Amount = -42m, Currency = "EUR", BookingDate = new DateOnly(2026, 8, 12),
                Counterparty = "Kartenzahlung", RawJson = "{}"
            });
            db.Purchases.Add(new Purchase
            {
                Id = space.Purchase, FullWorthSpaceId = space.Id, TransactionId = space.Transaction,
                Source = "receipt", Merchant = "ZAUBERSTAB GmbH", PurchaseDate = new DateOnly(2026, 8, 12),
                TotalAmount = 42m, Currency = "EUR", Status = "review", CreatedByUserId = space.Owner
            });
            await db.SaveChangesAsync();
        });
    }
}
