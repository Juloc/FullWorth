using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Collections;

/// <summary>
/// Sammlungen (#124): die zweite Zuordnungsachse neben der Kategorie.
///
/// Die Regel, die diese Tests vor allem halten, ist die gegen Doppelzaehlung. Eine Buchung darf zu
/// mehreren Sammlungen gehoeren - „Wohnung" UND „Badrenovierung" -, und dann zeigt JEDE Sammlung den
/// vollen Betrag. Was nicht passieren darf: dass die Buchung dadurch zweimal in einer Summe landet,
/// oder dass eine Zuordnung die andere ersetzt.
/// </summary>
public sealed class CollectionIntegrationTests
{
    [Fact]
    public async Task A_transaction_in_two_collections_counts_once_in_each()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var home = await CreateAsync(client, world, "Wohnung");
        var bath = await CreateAsync(client, world, "Badrenovierung");

        // Dieselbe Bauhaus-Buchung in beide Sammlungen.
        foreach (var collection in new[] { home, bath })
        {
            using var response = await client.SendAsync(Request(HttpMethod.Post,
                $"/api/collections/{collection:D}/transactions?fullWorthSpaceId={world.Space:D}", world.Owner,
                new { transactionIds = new[] { world.Bauhaus } }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var rows = await ListAsync(client, world);
        var homeRow = rows.Single(row => row.GetProperty("name").GetString() == "Wohnung");
        var bathRow = rows.Single(row => row.GetProperty("name").GetString() == "Badrenovierung");

        // Beide zeigen 184 - und jede zaehlt genau EINE Buchung, nicht zwei.
        Assert.Equal(184m, homeRow.GetProperty("expenses").GetDecimal());
        Assert.Equal(184m, bathRow.GetProperty("expenses").GetDecimal());
        Assert.Equal(1, homeRow.GetProperty("transactionCount").GetInt32());
        Assert.Equal(1, bathRow.GetProperty("transactionCount").GetInt32());
    }

    /// <summary>
    /// Zweimal dieselbe Zuordnung ist keine zweite Zuordnung. Ohne den zusammengesetzten Schluessel
    /// und ON CONFLICT DO NOTHING stuende die Buchung zweimal drin und die Summe waere doppelt.
    /// </summary>
    [Fact]
    public async Task Assigning_the_same_transaction_twice_changes_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var trip = await CreateAsync(client, world, "Gardasee 2026");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await client.SendAsync(Request(HttpMethod.Post,
                $"/api/collections/{trip:D}/transactions?fullWorthSpaceId={world.Space:D}", world.Owner,
                new { transactionIds = new[] { world.Hotel } }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var row = (await ListAsync(client, world)).Single();
        Assert.Equal(1, row.GetProperty("transactionCount").GetInt32());
        Assert.Equal(680m, row.GetProperty("expenses").GetDecimal());
    }

    /// <summary>
    /// „Hinzufuegen" ersetzt nicht. Wer aus der Buchungsliste heraus zwanzig Buchungen einer Sammlung
    /// zuordnet, sieht die anderen Sammlungen dieser Buchungen gar nicht - sie duerfen nicht
    /// stillschweigend verschwinden.
    /// </summary>
    [Fact]
    public async Task Adding_to_one_collection_keeps_the_others()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var home = await CreateAsync(client, world, "Wohnung");
        var bath = await CreateAsync(client, world, "Badrenovierung");

        await AssignAsync(client, world, home, world.Bauhaus);
        await AssignAsync(client, world, bath, world.Bauhaus);

        using var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/transactions/{world.Bauhaus:D}/collections?fullWorthSpaceId={world.Space:D}", world.Owner));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.EnumerateArray().Select(item => item.GetGuid()).ToHashSet();

        Assert.Equal(2, ids.Count);
        Assert.Contains(home, ids);
        Assert.Contains(bath, ids);
    }

    /// <summary>Eine Sammlung loeschen loescht keine Buchung - nur die Zuordnung.</summary>
    [Fact]
    public async Task Deleting_a_collection_leaves_the_transactions_alone()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var trip = await CreateAsync(client, world, "Gardasee 2026");
        await AssignAsync(client, world, trip, world.Hotel);

        using var deleted = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/collections/{trip:D}?fullWorthSpaceId={world.Space:D}", world.Owner));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.True(await db.Transactions.AnyAsync(row => row.Id == world.Hotel));
            Assert.False(await db.Set<FullWorth.Backend.Modules.Purchases.FinanceTag>()
                .AnyAsync(row => row.Id == trip));
        });
    }

    /// <summary>Die Kategorie-Aufteilung ist der Punkt, an dem beide Achsen zusammenkommen.</summary>
    [Fact]
    public async Task The_detail_splits_a_collection_by_category()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var trip = await CreateAsync(client, world, "Gardasee 2026");
        await AssignAsync(client, world, trip, world.Hotel);
        await AssignAsync(client, world, trip, world.Fuel);

        using var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/collections/{trip:D}?fullWorthSpaceId={world.Space:D}", world.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var categories = json.RootElement.GetProperty("categories").EnumerateArray().ToArray();
        Assert.Equal(2, categories.Length);
        // Absteigend nach Ausgaben: Hotel 680 vor Tanken 82.
        Assert.Equal("Hotel", categories[0].GetProperty("categoryName").GetString());
        Assert.Equal(680m, categories[0].GetProperty("expenses").GetDecimal());
        Assert.Equal("Tanken", categories[1].GetProperty("categoryName").GetString());
        Assert.Equal(82m, categories[1].GetProperty("expenses").GetDecimal());
    }

    /// <summary>
    /// Der Zeitraum ALLEIN macht keine Zugehoerigkeit. Eine Miete waehrend der Reise ist ein Kandidat
    /// (sie faellt in den Zeitraum), aber sie steht hinter dem Hotel, das ausserdem den Haendler
    /// teilt - und der Grund steht dabei, damit der Benutzer es beurteilen kann.
    /// </summary>
    [Fact]
    public async Task Candidates_name_their_reason_and_write_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var trip = await CreateAsync(client, world, "Gardasee 2026",
            start: new DateOnly(2026, 8, 12), end: new DateOnly(2026, 8, 17));
        await AssignAsync(client, world, trip, world.Hotel);

        using var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/collections/{trip:D}/candidates?fullWorthSpaceId={world.Space:D}", world.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var candidates = json.RootElement.EnumerateArray().ToArray();

        // Die bereits zugeordnete Buchung ist kein Kandidat mehr.
        Assert.DoesNotContain(candidates, item => item.GetProperty("transactionId").GetGuid() == world.Hotel);
        Assert.NotEmpty(candidates);
        foreach (var candidate in candidates)
            Assert.NotEmpty(candidate.GetProperty("reasons").EnumerateArray());

        // Und nichts wurde zugeordnet: die Sammlung enthaelt weiterhin genau eine Buchung.
        var row = (await ListAsync(client, world)).Single();
        Assert.Equal(1, row.GetProperty("transactionCount").GetInt32());
    }

    [Fact]
    public async Task A_second_collection_cannot_take_an_existing_name()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();
        await CreateAsync(client, world, "Wohnung");

        using var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/collections?fullWorthSpaceId={world.Space:D}", world.Owner,
            new { name = "  wohnung  " }));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_end_before_the_start_is_refused()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/collections?fullWorthSpaceId={world.Space:D}", world.Owner,
            new { name = "Rueckwaerts", startDate = "2026-08-17", endDate = "2026-08-12" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Die Sammlung als Filterdimension in der normalen Buchungsliste - der Weg zurueck. Ohne ihn ist
    /// die Zuordnung eine Einbahnstrasse: man kann Buchungen einsammeln, aber die Buchungsliste weiss
    /// nichts davon.
    /// </summary>
    [Fact]
    public async Task The_transaction_list_can_be_filtered_by_a_collection()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var trip = await CreateAsync(client, world, "Gardasee 2026");
        await AssignAsync(client, world, trip, world.Hotel);
        await AssignAsync(client, world, trip, world.Fuel);

        var ids = await SearchAsync(client, world, $"&collectionIds={trip:D}");

        Assert.Equal(2, ids.Length);
        Assert.Contains(world.Hotel, ids);
        Assert.Contains(world.Fuel, ids);
        Assert.DoesNotContain(world.Bauhaus, ids);
    }

    /// <summary>
    /// Mehrere Sammlungen heisst ODER. Die eigentliche Falle steckt in der Buchung, die in BEIDEN
    /// liegt: ein JOIN haette sie zweimal geliefert, und eine Buchungsliste mit Doppelgaengern ist
    /// schlimmer als gar kein Filter.
    /// </summary>
    [Fact]
    public async Task Two_collections_mean_or_and_a_transaction_in_both_appears_once()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var home = await CreateAsync(client, world, "Wohnung");
        var bath = await CreateAsync(client, world, "Badrenovierung");
        await AssignAsync(client, world, home, world.Bauhaus);
        await AssignAsync(client, world, bath, world.Bauhaus);
        await AssignAsync(client, world, home, world.Hotel);

        var ids = await SearchAsync(client, world, $"&collectionIds={home:D}&collectionIds={bath:D}");

        Assert.Equal(2, ids.Length);
        Assert.Single(ids, id => id == world.Bauhaus);
        Assert.Contains(world.Hotel, ids);
    }

    /// <summary>
    /// Eine fremde Sammlungskennung darf nicht einfach ignoriert werden - dann kaeme die ungefilterte
    /// Liste zurueck. Sie liefert nichts, und schon die Trefferzahl verraet damit nichts.
    /// </summary>
    [Fact]
    public async Task A_collection_from_another_space_matches_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        var mine = await SeedAsync(factory);
        var theirs = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var foreign = await CreateAsync(client, theirs, "Fremde Sammlung");
        await AssignAsync(client, theirs, foreign, theirs.Hotel);

        Assert.Empty(await SearchAsync(client, mine, $"&collectionIds={foreign:D}"));
    }

    private static async Task<Guid[]> SearchAsync(HttpClient client, World world, string extraQuery)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/transactions?fullWorthSpaceId={world.Space:D}&limit=200{extraQuery}", world.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();
    }

    private static async Task<Guid> CreateAsync(
        HttpClient client, World world, string name, DateOnly? start = null, DateOnly? end = null)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/collections?fullWorthSpaceId={world.Space:D}", world.Owner,
            new
            {
                name,
                startDate = start?.ToString("yyyy-MM-dd"),
                endDate = end?.ToString("yyyy-MM-dd")
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task AssignAsync(HttpClient client, World world, Guid collection, Guid transaction)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/collections/{collection:D}/transactions?fullWorthSpaceId={world.Space:D}", world.Owner,
            new { transactionIds = new[] { transaction } }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client, World world)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/collections?fullWorthSpaceId={world.Space:D}", world.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record World(Guid Space, Guid Owner, Guid Bauhaus, Guid Hotel, Guid Fuel);

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var hardware = Guid.NewGuid();
        var hotel = Guid.NewGuid();
        var fuel = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = world.Space,
                Name = "Sammlungen",
                BaseCurrency = "EUR",
                DefaultCategoriesSeededAt = DateTimeOffset.UtcNow,
                DefaultCategoryLanguage = "de"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space,
                UserId = world.Owner,
                Role = FullWorthSpaceRoles.Owner
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
                IdentificationHash = $"hash-{accountId:N}",
                ProviderAccountId = $"acc-{accountId:N}",
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
            db.Categories.AddRange(
                new FinanceCategory { Id = hardware, FullWorthSpaceId = world.Space, Key = "shopping.hardware", Name = "Baumarkt" },
                new FinanceCategory { Id = hotel, FullWorthSpaceId = world.Space, Key = "travel.accommodation", Name = "Hotel" },
                new FinanceCategory { Id = fuel, FullWorthSpaceId = world.Space, Key = "vehicle.fuel", Name = "Tanken" });

            db.Transactions.AddRange(
                Transaction(world.Bauhaus, accountId, hardware, new DateOnly(2026, 8, 20), -184m, "BAUHAUS"),
                Transaction(world.Hotel, accountId, hotel, new DateOnly(2026, 8, 13), -680m, "HOTEL GARDA"),
                Transaction(world.Fuel, accountId, fuel, new DateOnly(2026, 8, 14), -82m, "AGIP"),
                // Eine Buchung IM Reisezeitraum, die nicht zur Reise gehoert: die Miete laeuft weiter.
                Transaction(Guid.NewGuid(), accountId, null, new DateOnly(2026, 8, 15), -900m, "VERMIETER"));
            await db.SaveChangesAsync();
        });
        return world;
    }

    private static FinanceTransaction Transaction(
        Guid id, Guid accountId, Guid? categoryId, DateOnly date, decimal amount, string counterparty) =>
        new()
        {
            Id = id,
            AccountId = accountId,
            CategoryId = categoryId,
            ExternalKey = $"key-{id:N}",
            Status = "BOOK",
            BookingDate = date,
            ValueDate = date,
            Amount = amount,
            Currency = "EUR",
            Counterparty = counterparty,
            NormalizedCounterparty = counterparty.ToUpperInvariant()
        };
}
