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
/// Wortindex statt <c>%ILIKE%</c> ueber vier Spalten (#161).
///
/// Gemessen an 200 000 Buchungen: ein Suchbegriff OHNE Treffer kostete 156 ms, weil jede Zeile
/// gelesen und verworfen wurde - und genau das passiert beim Tippen, denn jedes Zwischenergebnis
/// einer Eingabe ist ein Suchbegriff ohne Treffer. Mit Wortindex sind es 0,14 ms.
///
/// Zwei Dinge, die diese Tests festhalten: dass die Suche weiterhin findet, was sie finden soll, und
/// die eine Sache, die sie NICHT mehr findet - das ist der Preis einer Wortsuche und soll nicht
/// unbemerkt bleiben.
/// </summary>
public sealed class TransactionSearchTests
{
    [Theory]
    [InlineData("REWE", 1)]       // ganzes Wort
    [InlineData("rewe", 1)]       // Gross-/Kleinschreibung egal
    [InlineData("REW", 1)]        // Wortanfang - das ist der Fall beim Tippen
    [InlineData("SAGT", 1)]       // ein Wort mitten in der Gegenpartei
    [InlineData("girocard", 1)]   // aus dem Verwendungszweck
    [InlineData("Urlaub", 1)]     // aus der eigenen Notiz
    [InlineData("GIBTESNICHT", 0)]
    public async Task The_search_finds_what_it_should(string term, int expected)
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(expected, await CountAsync(client, world, term));
    }

    /// <summary>
    /// Der Preis der Wortsuche: gesucht wird ab Wortanfang, nicht mitten im Wort. "EWE" findet "REWE"
    /// nicht mehr. Das ist genau der Unterschied, der aus 156 ms 0,14 ms macht - und er steht hier,
    /// damit er eine Entscheidung bleibt und nicht als Fehler gemeldet wird.
    /// </summary>
    [Fact]
    public async Task Searching_inside_a_word_no_longer_matches()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(0, await CountAsync(client, world, "EWE"));
    }

    /// <summary>Mehrere Woerter heisst UND - dieselbe Erwartung, die jeder an eine Suchzeile hat.</summary>
    [Theory]
    [InlineData("REWE SAGT", 1)]
    [InlineData("REWE NETFLIX", 0)]
    public async Task Several_words_all_have_to_match(string term, int expected)
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        Assert.Equal(expected, await CountAsync(client, world, term));
    }

    /// <summary>
    /// Die Syntax von <c>to_tsquery</c> darf nicht aus der Eingabe kommen. Ein Doppelpunkt oder eine
    /// Klammer im Suchfeld waere sonst Syntax - und eine ungueltige Abfrage wirft, statt nichts zu
    /// finden. Die Suche wuerde bei bestimmten Zeichen einen Fehler zeigen.
    /// </summary>
    [Theory]
    [InlineData("REWE:*")]
    [InlineData("REWE & NETFLIX")]
    [InlineData("!REWE")]
    [InlineData("(REWE")]
    public async Task Query_syntax_in_the_input_is_text_and_never_syntax(string term)
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // Kein Fehler, und "REWE" wird als Wort erkannt - das zweite Wort schraenkt ggf. ein.
        using var response = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={world.Space:D}&query={Uri.EscapeDataString(term)}", world.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Eine Eingabe ganz ohne Buchstaben und Ziffern schraenkt nichts ein - und wirft nichts.</summary>
    [Theory]
    [InlineData("&&&")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void An_input_without_anything_searchable_becomes_nothing(string term)
    {
        Assert.Null(TransactionSearchTerm.ToPrefixQuery(term));
    }

    [Fact]
    public void Words_become_quoted_prefixes_joined_with_and()
    {
        Assert.Equal("'rewe':*", TransactionSearchTerm.ToPrefixQuery("rewe"));
        Assert.Equal("'rewe':* & 'markt':*", TransactionSearchTerm.ToPrefixQuery("rewe markt"));
        // Syntaxzeichen fallen weg, statt Syntax zu werden.
        Assert.Equal("'rewe':*", TransactionSearchTerm.ToPrefixQuery("!rewe:*"));
    }

    private static async Task<int> CountAsync(HttpClient client, World world, string term)
    {
        using var response = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={world.Space:D}&query={Uri.EscapeDataString(term)}", world.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").GetArrayLength();
    }

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record World(Guid Space, Guid Owner);

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Suche", BaseCurrency = "EUR" });
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

            // Eine Zeile, die jedes durchsuchte Feld benutzt, und eine zweite, die keines davon teilt.
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = accountId,
                ExternalKey = "S-1",
                Amount = -12.34m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 9, 1),
                Counterparty = "REWE SAGT DANKE",
                NormalizedCounterparty = "REWE",
                Description = "Kartenzahlung girocard",
                UserNote = "Einkauf vor dem Urlaub",
                RawJson = "{}"
            });
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = accountId,
                ExternalKey = "S-2",
                Amount = -9.99m,
                Currency = "EUR",
                BookingDate = new DateOnly(2026, 9, 2),
                Counterparty = "NETFLIX INTERNATIONAL",
                NormalizedCounterparty = "NETFLIX",
                Description = "Abo",
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        });
        return world;
    }
}
