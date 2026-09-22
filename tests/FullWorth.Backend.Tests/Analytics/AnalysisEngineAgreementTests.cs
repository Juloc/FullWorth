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

namespace FullWorth.Backend.Tests.Analytics;

/// <summary>
/// Zwei Auswertungsmaschinen, und nur eine wird benutzt - und die zweite ist sogar dreifach begraben.
///
/// <c>GET /api/analytics/chart</c> kann vier Kennzahlen ueber vier Dimensionen und haengt an der
/// Auswertungsseite. <c>POST /api/analytics/query</c> kann sechs ueber zehn, hat dazu ein
/// Fluss-Diagramm und gespeicherte Auswertungen samt vollem CRUD - und <b>keinen einzigen Aufrufer
/// im Frontend</b>.
///
/// Dazu kommt: der Rumpf von <c>AnalysisQueryEndpoints.Query</c> laeuft gar nicht.
/// <c>FinancialReconciliationMiddleware</c> faengt die Route vorher ab und beantwortet sie aus
/// <c>FinancialReconciliationReportService.AnalyticsAsync</c>. Gefunden, weil eine Korrektur in der
/// einen Fassung an der Antwort nichts aenderte - dieselbe Bauart wie bei den Budget-Uebertraegen,
/// wo eine Middleware die Fassung verdeckte, die die Funktion hatte.
///
/// Solange beide existieren, muessen sie wenigstens dasselbe sagen. Genau eine Abweichung kam heraus
/// und ist behoben: die reichere Maschine zeigte bei spend eine Kategorie mit 0 Euro - einen leeren
/// Balken fuer eine Kategorie, in der nichts ausgegeben wurde.
///
/// Dieser Test ist die Voraussetzung dafuer, die Seite spaeter gefahrlos auf die reichere Maschine
/// umzustellen: wer die Maschine wechselt, ohne zu wissen, ob die Zahlen gleich sind, verschiebt
/// Zahlen, die jemand kennt.
/// </summary>
public sealed class AnalysisEngineAgreementTests
{
    [Theory]
    [InlineData("spend", "month")]
    [InlineData("spend", "category")]
    [InlineData("income", "month")]
    [InlineData("net", "month")]
    [InlineData("count", "category")]
    public async Task Both_engines_report_the_same_totals(string measure, string dimension)
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var chart = await ValuesAsync(client, world, measure, dimension, viaQuery: false);
        var query = await ValuesAsync(client, world, measure, dimension, viaQuery: true);

        // Verglichen werden die Zahlen, nicht ihre Beschriftungen: die eine Maschine liefert Key und
        // Label, die andere nur einen Key. Was auf dem Bildschirm zaehlt, sind die Werte - und wenn
        // die beiden ueber denselben Daten auseinanderlaufen, zeigt die Seite je nach Maschine etwas
        // anderes an.
        Assert.NotEmpty(chart);
        Assert.True(chart.SequenceEqual(query), $"chart={string.Join(",", chart)} query={string.Join(",", query)}");
    }

    private static async Task<List<decimal>> ValuesAsync(
        HttpClient client, World world, string measure, string dimension, bool viaQuery)
    {
        HttpRequestMessage request;
        if (viaQuery)
        {
            request = Request(HttpMethod.Post, $"/api/analytics/query?fullWorthSpaceId={world.Space:D}", world.Owner);
            request.Content = JsonContent.Create(new { measure, dimension, from = "2026-01-01", to = "2026-12-31" });
        }
        else
        {
            request = Request(HttpMethod.Get,
                $"/api/analytics/chart?fullWorthSpaceId={world.Space:D}&measure={measure}&dimension={dimension}" +
                "&from=2026-01-01&to=2026-12-31", world.Owner);
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("series", out var series), body);
        return series.EnumerateArray()
            .Select(point => point.GetProperty("value").GetDecimal())
            .OrderBy(value => value)
            .ToList();
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
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
        var food = Guid.NewGuid();
        var rent = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Auswertung", BaseCurrency = "EUR" });
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
            db.Categories.Add(new FinanceCategory
            {
                Id = food, FullWorthSpaceId = world.Space, Key = "food", Name = "Lebensmittel", SortOrder = 1
            });
            db.Categories.Add(new FinanceCategory
            {
                Id = rent, FullWorthSpaceId = world.Space, Key = "rent", Name = "Wohnen", SortOrder = 2
            });

            void Add(string key, DateOnly date, decimal amount, Guid? category)
                => db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = accountId,
                    ExternalKey = key,
                    Amount = amount,
                    Currency = "EUR",
                    BookingDate = date,
                    Counterparty = amount < 0 ? "REWE" : "Arbeitgeber AG",
                    NormalizedCounterparty = amount < 0 ? "REWE" : "ARBEITGEBER AG",
                    CategoryId = category,
                    RawJson = "{}"
                });

            Add("a-1", new DateOnly(2026, 1, 15), -42.19m, food);
            Add("a-2", new DateOnly(2026, 1, 28), -820.00m, rent);
            Add("a-3", new DateOnly(2026, 2, 10), -17.50m, food);
            Add("a-4", new DateOnly(2026, 2, 28), -820.00m, rent);
            Add("a-5", new DateOnly(2026, 1, 31), 2810.44m, null);
            Add("a-6", new DateOnly(2026, 2, 28), 2810.44m, null);
            await db.SaveChangesAsync();
        });
        return world;
    }
}
