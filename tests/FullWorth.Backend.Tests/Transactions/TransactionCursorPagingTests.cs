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
/// Cursor statt OFFSET (#161) - und der Fehler, den der Umbau nebenbei aufgedeckt hat.
///
/// <b>Die Sortierung hatte keinen eindeutigen letzten Schluessel.</b> Sie ging nach "vorgemerkt?",
/// dann Datum, dann <c>UpdatedAt</c> - und keines davon ist eindeutig. Ein Import legt dutzende
/// Buchungen mit demselben Datum und demselben Zeitstempel an; zwischen ihnen war die Reihenfolge
/// beliebig, und zwar bei JEDER Abfrage neu. Damit konnte schon das bisherige <c>Skip/Take</c>
/// Zeilen ueberspringen oder doppelt zeigen. Der Cursor hat den Fehler nicht verursacht, sondern
/// sichtbar gemacht.
///
/// Die Seeds hier sind deshalb absichtlich gemein: 60 Buchungen, alle am selben Tag, alle mit
/// demselben <c>UpdatedAt</c>. Genau so sieht ein frischer Kontoauszugsimport aus.
/// </summary>
public sealed class TransactionCursorPagingTests
{
    private const int Total = 60;
    private const int PageSize = 25;

    /// <summary>
    /// Der Kern: durchblaettern liefert jede Buchung genau einmal. Nicht 59, nicht 61, und keine
    /// zweimal - auch wenn jede Sortierspalte gleich ist.
    /// </summary>
    [Fact]
    public async Task Paging_through_identical_rows_yields_every_transaction_exactly_once()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var seen = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var url = $"/api/transactions?fullWorthSpaceId={world.Space:D}&limit={PageSize}" +
                      (cursor is null ? string.Empty : $"&after={Uri.EscapeDataString(cursor)}");
            using var response = await client.SendAsync(Request(url, world.Owner));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            seen.AddRange(json.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()));

            if (!json.RootElement.GetProperty("hasNext").GetBoolean()) break;
            cursor = json.RootElement.GetProperty("nextCursor").GetString();
            Assert.NotNull(cursor);
        }

        Assert.Equal(Total, seen.Count);
        Assert.Equal(Total, seen.Distinct().Count());
    }

    /// <summary>
    /// Der erste Aufruf hat keinen Cursor und braucht trotzdem die Gesamtzahl - die Oberflaeche zeigt
    /// sie. Beim Nachladen entfaellt sie: eine Gesamtzahl je Seite zu zaehlen kostet dieselbe Arbeit
    /// wie die Seite selbst, und Infinite Scroll braucht sie nicht.
    /// </summary>
    [Fact]
    public async Task The_first_page_counts_and_the_following_ones_do_not()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={world.Space:D}&limit={PageSize}", world.Owner));
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(Total, firstJson.RootElement.GetProperty("total").GetInt32());
        Assert.True(firstJson.RootElement.GetProperty("hasNext").GetBoolean());

        var cursor = firstJson.RootElement.GetProperty("nextCursor").GetString();
        using var second = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={world.Space:D}&limit={PageSize}&after={Uri.EscapeDataString(cursor!)}",
            world.Owner));
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("total").ValueKind);
        Assert.Equal(PageSize, secondJson.RootElement.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// Die letzte Seite sagt, dass Schluss ist - ohne einen Cursor, der ins Leere zeigt. Ein
    /// nextCursor auf der letzten Seite waere eine Einladung zu einer Anfrage, die nichts liefert.
    /// </summary>
    [Fact]
    public async Task The_last_page_reports_the_end_and_offers_no_cursor()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={world.Space:D}&limit=200", world.Owner));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(Total, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(json.RootElement.GetProperty("hasNext").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nextCursor").ValueKind);
    }

    /// <summary>
    /// Ein kaputter Cursor faengt nicht mitten in der Liste an. Er wird verworfen, und die Antwort
    /// ist die erste Seite - sichtbar von vorn, statt still einen Teil zu ueberspringen.
    /// </summary>
    [Fact]
    public async Task A_broken_cursor_is_ignored_instead_of_starting_somewhere()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={world.Space:D}&limit={PageSize}&after=nonsense", world.Owner));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(Total, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(PageSize, json.RootElement.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// Ein Schluessel in der richtigen Form geht durch - er IST der Vergleichswert, also darf an ihm
    /// nichts veraendert werden ausser Leerraum aussen herum.
    /// </summary>
    [Fact]
    public void A_well_formed_key_passes_through_unchanged()
    {
        var key = "0" + new string('7', 8) + new string('1', 20) + new string('a', 32);

        Assert.Equal(key, TransactionCursor.Normalize(key));
        Assert.Equal(key, TransactionCursor.Normalize("  " + key + "  "));
    }

    /// <summary>
    /// Alles andere ist kein Cursor. Das ist kein Formalismus: ein beliebiger Text waere ein
    /// gueltiger Vergleichswert - "&lt; nonsense" trifft jede Zeile, deren Schluessel mit einer Ziffer
    /// beginnt. Die Anfrage saehe erfolgreich aus und begaenne irgendwo.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("2" + "00000000" + "00000000000000000000" + "00000000000000000000000000000000")]  // erstes Zeichen
    [InlineData("0" + "0000000" + "00000000000000000000" + "00000000000000000000000000000000")]   // zu kurz
    [InlineData("0" + "000000x0" + "00000000000000000000" + "00000000000000000000000000000000")]  // keine Ziffer
    public void Anything_that_is_not_a_key_is_refused(string value)
    {
        Assert.Null(TransactionCursor.Normalize(value));
    }

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record World(Guid Space, Guid Owner, Guid Account);

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        // Derselbe Tag, derselbe Zeitstempel: so sieht ein frischer Kontoauszugsimport aus, und genau
        // hier war die Reihenfolge ohne eindeutigen letzten Schluessel beliebig.
        var sameDay = new DateOnly(2026, 9, 1);
        var sameMoment = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Cursor", BaseCurrency = "EUR" });
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

            for (var index = 0; index < Total; index++)
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = world.Account,
                    ExternalKey = $"C-{index:000}",
                    Amount = -(index + 1),
                    Currency = "EUR",
                    BookingDate = sameDay,
                    Counterparty = $"HAENDLER {index:000}",
                    NormalizedCounterparty = $"HAENDLER {index:000}",
                    UpdatedAt = sameMoment,
                    RawJson = "{}"
                });
            }
            await db.SaveChangesAsync();
        });
        return world;
    }
}
