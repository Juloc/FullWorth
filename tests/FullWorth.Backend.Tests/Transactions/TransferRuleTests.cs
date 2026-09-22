using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// #146: aus einer bestaetigten Umbuchung lernen.
///
/// Vorher gab es kein Gedaechtnis. <c>TransferDetectionService</c> leitete bei jedem Lauf alles neu
/// aus Betrag, Drei-Tage-Fenster und Kontokennung ab; eine Bestaetigung des Benutzers hinterliess
/// nichts. Solange ein Fall in diese Mechanik passt, faellt das nicht auf - er wird jedes Mal wieder
/// gefunden. Es faellt dort auf, wo sie nicht greift, und der haerteste Fall davon ist der ohne
/// Gegenbuchung: eine Ueberweisung auf das Sparkonto ausser Haus.
/// </summary>
public sealed class TransferRuleTests
{
    /// <summary>
    /// Verknuepfen lernt BEIDE Richtungen. Eine Umbuchung sieht von jedem der zwei Konten aus anders
    /// aus - wer sie nur einmal lernt, erkennt die Gegenseite beim naechsten Mal wieder nicht.
    /// </summary>
    [Fact]
    public async Task Confirming_a_link_learns_the_relation_from_both_accounts()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{world.Outgoing:D}/transfer-link?fullWorthSpaceId={world.Space:D}",
            world.Owner, new { otherTransactionId = world.Incoming }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var rules = await RulesAsync(client, world);
        Assert.Equal(2, rules.Length);
        Assert.Contains(rules, rule =>
            rule.GetProperty("accountId").GetGuid() == world.Checking &&
            rule.GetProperty("targetAccountId").GetGuid() == world.Savings);
        Assert.Contains(rules, rule =>
            rule.GetProperty("accountId").GetGuid() == world.Savings &&
            rule.GetProperty("targetAccountId").GetGuid() == world.Checking);
    }

    /// <summary>
    /// Der zweite Fall des Issues: kein Gegenkonto, weil es in FullWorth nicht gefuehrt wird. Die
    /// Buchung bleibt fachlich eine Umbuchung - und <c>targetAccountId</c> ist NULL, statt dass
    /// irgendein Konto erfunden wird.
    /// </summary>
    [Fact]
    public async Task An_external_transfer_is_remembered_without_inventing_a_counter_account()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{world.External:D}/transfer-external?fullWorthSpaceId={world.Space:D}",
            world.Owner, new { purpose = "Sparkonto extern" }));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var rule = Assert.Single(await RulesAsync(client, world));
        Assert.Equal(JsonValueKind.Null, rule.GetProperty("targetAccountId").ValueKind);
        Assert.Equal("IKANO BANK", rule.GetProperty("normalizedCounterparty").GetString());

        // Und die Buchung selbst: Umbuchung, also ausserhalb jeder Einnahmen-/Ausgabenauswertung -
        // dort wird auf IsTransfer gefiltert, nicht auf eine Gruppe.
        await factory.SeedAsync(async db =>
        {
            var transaction = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == world.External);
            Assert.True(transaction.IsTransfer);
            Assert.Null(transaction.TransferGroupId);
            Assert.Equal("Sparkonto extern", transaction.TransferPurpose);
        });
    }

    /// <summary>
    /// Wer schon eine Gegenbuchung hat, hat kein externes Ziel. Beides gleichzeitig zu behaupten
    /// waere ein Widerspruch, den spaeter niemand aufloest.
    /// </summary>
    [Fact]
    public async Task A_linked_transfer_cannot_also_be_declared_external()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var link = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{world.Outgoing:D}/transfer-link?fullWorthSpaceId={world.Space:D}",
            world.Owner, new { otherTransactionId = world.Incoming }));
        Assert.Equal(HttpStatusCode.NoContent, link.StatusCode);

        using var external = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{world.Outgoing:D}/transfer-external?fullWorthSpaceId={world.Space:D}",
            world.Owner, new { purpose = (string?)null }));
        Assert.Equal(HttpStatusCode.BadRequest, external.StatusCode);
    }

    /// <summary>
    /// Die juengere Entscheidung gilt. Wer erst zu Konto A verknuepft und spaeter zu Konto B, hat
    /// seine Meinung geaendert - die Regel ist sein Gedaechtnis, nicht ihr eigenes. Und sie bleibt
    /// EINE Regel; zwei widersprechende waeren schlimmer als keine.
    /// </summary>
    [Fact]
    public async Task A_later_decision_replaces_the_earlier_one_instead_of_adding_to_it()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{world.External:D}/transfer-external?fullWorthSpaceId={world.Space:D}",
            world.Owner, new { purpose = "extern" }));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // Dieselbe Gegenpartei auf demselben Konto, jetzt aber mit Gegenbuchung.
        using var second = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{world.ExternalTwin:D}/transfer-link?fullWorthSpaceId={world.Space:D}",
            world.Owner, new { otherTransactionId = world.Incoming }));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        var rules = await RulesAsync(client, world);
        var checking = Assert.Single(rules, rule =>
            rule.GetProperty("accountId").GetGuid() == world.Checking &&
            rule.GetProperty("normalizedCounterparty").GetString() == "IKANO BANK");
        Assert.Equal(world.Savings, checking.GetProperty("targetAccountId").GetGuid());
    }

    /// <summary>
    /// Eine gelernte Regel, die man nicht mehr los wird, ist eine Entscheidung, die der Benutzer
    /// einmal getroffen hat und nie zuruecknehmen kann - und sie wirkt auf jede kuenftige Buchung
    /// desselben Musters.
    /// </summary>
    [Fact]
    public async Task A_learned_rule_can_be_deleted_again()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var mark = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{world.External:D}/transfer-external?fullWorthSpaceId={world.Space:D}",
            world.Owner, new { purpose = (string?)null }));
        Assert.Equal(HttpStatusCode.NoContent, mark.StatusCode);
        var rule = Assert.Single(await RulesAsync(client, world));

        using var delete = await client.SendAsync(Request(HttpMethod.Delete,
            $"/api/transfers/rules/{rule.GetProperty("id").GetGuid():D}?fullWorthSpaceId={world.Space:D}",
            world.Owner));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await RulesAsync(client, world));
    }

    /// <summary>Fremde Regeln sieht niemand - auch nicht ihre Anzahl.</summary>
    [Fact]
    public async Task Rules_of_another_space_are_invisible()
    {
        using var factory = new BackendWebApplicationFactory();
        var mine = await SeedAsync(factory);
        var theirs = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var mark = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/transactions/{theirs.External:D}/transfer-external?fullWorthSpaceId={theirs.Space:D}",
            theirs.Owner, new { purpose = (string?)null }));
        Assert.Equal(HttpStatusCode.NoContent, mark.StatusCode);

        Assert.Empty(await RulesAsync(client, mine));
    }

    private static async Task<JsonElement[]> RulesAsync(HttpClient client, World world)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/transfers/rules?fullWorthSpaceId={world.Space:D}", world.Owner));
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

    private sealed record World(
        Guid Space, Guid Owner, Guid Checking, Guid Savings,
        Guid Outgoing, Guid Incoming, Guid External, Guid ExternalTwin);

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory)
    {
        var world = new World(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
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
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = world.Space,
                Name = "Umbuchungen",
                BaseCurrency = "EUR"
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

            foreach (var (id, name) in new[] { (world.Checking, "Girokonto"), (world.Savings, "Tagesgeld") })
            {
                db.Accounts.Add(new FinanceAccount
                {
                    Id = id,
                    FullWorthSpaceId = world.Space,
                    BankConnectionId = connectionId,
                    IdentificationHash = $"hash-{id:N}",
                    ProviderAccountId = $"acc-{id:N}",
                    InstitutionName = "Testbank",
                    DisplayName = name,
                    Currency = "EUR",
                    IsActive = true
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = id,
                    UserId = world.Owner,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
            }

            db.Transactions.AddRange(
                Transaction(world.Outgoing, world.Checking, -500m, new DateOnly(2026, 9, 1), "TAGESGELD"),
                Transaction(world.Incoming, world.Savings, 500m, new DateOnly(2026, 9, 1), "GIROKONTO"),
                // Das externe Sparkonto: eine Ausgabe ohne Gegenbuchung irgendwo in FullWorth.
                Transaction(world.External, world.Checking, -300m, new DateOnly(2026, 9, 5), "IKANO BANK"),
                Transaction(world.ExternalTwin, world.Checking, -500m, new DateOnly(2026, 9, 8), "IKANO BANK"));
            await db.SaveChangesAsync();
        });
        return world;
    }

    private static FinanceTransaction Transaction(
        Guid id, Guid accountId, decimal amount, DateOnly date, string counterparty) =>
        new()
        {
            Id = id,
            AccountId = accountId,
            ExternalKey = $"T-{id:N}",
            Amount = amount,
            Currency = "EUR",
            BookingDate = date,
            Counterparty = counterparty,
            NormalizedCounterparty = counterparty,
            RawJson = "{}"
        };
}
