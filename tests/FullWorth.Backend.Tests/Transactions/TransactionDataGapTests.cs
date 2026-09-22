using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// Luecken in der Buchungshistorie (#131, Abschnitt 13).
///
/// Der Hinweis ist nur etwas wert, wenn er nicht staendig kommt. Diese Tests halten deshalb vor allem
/// fest, wann KEINE Luecke gemeldet wird: bei einem Konto mit von Haus aus grossen Abstaenden, am Rand
/// der Daten, und bei zu wenig Historie fuer einen Rhythmus.
/// </summary>
public sealed class TransactionDataGapTests
{
    /// <summary>
    /// Taeglich gebucht, dann drei Wochen nichts, dann wieder taeglich. Genau der Fall aus dem Issue:
    /// die Bankverbindung war zeitweise getrennt.
    /// </summary>
    [Fact]
    public async Task A_break_in_a_daily_history_is_reported()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, Daily(new DateOnly(2026, 5, 1), 20)
            .Concat(Daily(new DateOnly(2026, 6, 12), 20)).ToArray());
        using var client = factory.CreateClient();

        var gaps = await GapsAsync(client, world);

        var gap = Assert.Single(gaps);
        Assert.Equal("2026-05-20", gap.GetProperty("from").GetString());
        Assert.Equal("2026-06-12", gap.GetProperty("to").GetString());
        Assert.Equal(23, gap.GetProperty("days").GetInt32());
        Assert.Equal(world.Account.ToString(), gap.GetProperty("accountId").GetString());
    }

    /// <summary>
    /// Ein Konto, auf dem alle drei Wochen etwas gebucht wird, hat keine Luecke - es hat einen
    /// Rhythmus. Eine feste Tagesgrenze haette hier bei jedem Abstand angeschlagen und den Hinweis
    /// wertlos gemacht.
    /// </summary>
    [Fact]
    public async Task An_account_that_only_ever_books_every_three_weeks_has_no_gap()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, Every(new DateOnly(2026, 1, 5), 21, 20));
        using var client = factory.CreateClient();

        Assert.Empty(await GapsAsync(client, world));
    }

    /// <summary>
    /// Vor der ersten und nach der letzten Buchung ist keine Luecke, sondern der Rand der Daten. Dass
    /// ein Konto seit Monaten nichts mehr liefert, ist eine andere Aussage - sie steht als Datenstand
    /// am Konto und soll hier nicht ein zweites Mal, als etwas anderes, erscheinen.
    /// </summary>
    [Fact]
    public async Task The_edges_of_the_history_are_not_a_gap()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, Daily(new DateOnly(2026, 1, 1), 30));
        using var client = factory.CreateClient();

        Assert.Empty(await GapsAsync(client, world));
    }

    /// <summary>Aus fuenf Buchungen laesst sich kein Rhythmus ablesen; ein Hinweis daraus waere geraten.</summary>
    [Fact]
    public async Task Too_little_history_yields_no_guess()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory,
            [.. Daily(new DateOnly(2026, 5, 1), 3), .. Daily(new DateOnly(2026, 8, 1), 3)]);
        using var client = factory.CreateClient();

        Assert.Empty(await GapsAsync(client, world));
    }

    /// <summary>
    /// Eine ignorierte Buchung zaehlt nicht als Lebenszeichen des Kontos - sonst schloesse eine
    /// einzelne ausgeblendete Zeile mitten in der Luecke den Hinweis ab, ohne dass die Daten
    /// vollstaendiger geworden waeren.
    /// </summary>
    [Fact]
    public async Task An_ignored_booking_does_not_close_a_gap()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory,
            [.. Daily(new DateOnly(2026, 5, 1), 20), .. Daily(new DateOnly(2026, 6, 12), 20)],
            ignored: new DateOnly(2026, 5, 31));
        using var client = factory.CreateClient();

        var gap = Assert.Single(await GapsAsync(client, world));
        Assert.Equal(23, gap.GetProperty("days").GetInt32());
    }

    /// <summary>
    /// Fremder Bestand bleibt fremd. Geprueft wird mit einem ECHTEN zweiten Benutzer, der nur kein
    /// Mitglied dieses Space ist - eine erfundene Kennung waere schon an der Anmeldung gescheitert und
    /// haette ueber die Sichtbarkeit nichts ausgesagt.
    /// </summary>
    [Fact]
    public async Task Another_users_history_is_not_visible()
    {
        using var factory = new BackendWebApplicationFactory();
        var stranger = Guid.NewGuid();
        var world = await SeedAsync(factory, [.. Daily(new DateOnly(2026, 5, 1), 20), .. Daily(new DateOnly(2026, 6, 12), 20)], stranger: stranger);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            $"/api/transactions/data-gaps?fullWorthSpaceId={world.Space:D}", stranger));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, json.RootElement.GetArrayLength());
    }

    private static DateOnly[] Daily(DateOnly start, int count) => Every(start, 1, count);

    private static DateOnly[] Every(DateOnly start, int step, int count) =>
        Enumerable.Range(0, count).Select(index => start.AddDays(index * step)).ToArray();

    private static async Task<JsonElement[]> GapsAsync(HttpClient client, World world)
    {
        using var response = await client.SendAsync(Request(
            $"/api/transactions/data-gaps?fullWorthSpaceId={world.Space:D}", world.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
    }

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record World(Guid Space, Guid Owner, Guid Account);

    private static async Task<World> SeedAsync(
        BackendWebApplicationFactory factory, DateOnly[] days, DateOnly? ignored = null, Guid? stranger = null)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var connectionId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            if (stranger is not null)
                db.Users.Add(new FullWorthUser
                {
                    Id = stranger.Value,
                    EmailNormalized = $"{stranger.Value:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = "Fremd",
                    IsActive = true
                });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Luecken", BaseCurrency = "EUR" });
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
                Id = world.Account,
                FullWorthSpaceId = world.Space,
                BankConnectionId = connectionId,
                IdentificationHash = $"h-{world.Account:N}",
                ProviderAccountId = $"a-{world.Account:N}",
                InstitutionName = "Testbank",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = world.Account,
                UserId = world.Owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });

            var all = ignored is null ? days : [.. days, ignored.Value];
            foreach (var day in all)
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = world.Account,
                    ExternalKey = $"g-{day:yyyyMMdd}",
                    Amount = -5m,
                    Currency = "EUR",
                    BookingDate = day,
                    Counterparty = "Shop",
                    IsIgnored = ignored is not null && day == ignored.Value,
                    RawJson = "{}"
                });
            await db.SaveChangesAsync();
        });
        return world;
    }
}
