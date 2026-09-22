using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Budgets;

/// <summary>
/// #115: ab WANN der Uebertrag gerechnet wird - drei Modi, die leicht miteinander verwechselt werden.
///
/// Das ist nicht der Periodenbeginn. Die Periode sagt, wie lang ein Fenster ist; diese Angabe sagt,
/// ab welchem Fenster ueberhaupt gerechnet wird. Der Unterschied wird erst an einem Budget sichtbar,
/// das laenger existiert als die Periode: „so weit zurueck wie moeglich" summiert jede abgeschlossene
/// Periode seit dem Start auf, „diese Periode" faengt bei null an, und ein eigenes Datum liegt
/// dazwischen.
///
/// Die drei standen in <c>BudgetStore.BudgetActiveFrom</c> fertig da und hatten keinen einzigen Test.
/// Eine Verwechslung faellt in der Oberflaeche nicht auf - man sieht eine Zahl, nur eben die falsche.
///
/// Der Aufbau ist absichtlich karg: ein Budget ueber 100 pro Monat, seit Mai, und NICHTS wird
/// ausgegeben. Dann traegt jede abgeschlossene Periode genau +100 bei, und die erwartete Zahl ist
/// abzaehlbar statt ausgerechnet.
/// </summary>
public sealed class BudgetCarryOverStartTests
{
    private static readonly DateOnly Start = new(2026, 5, 1);
    private const string AsOf = "2026-08-15";

    /// <summary>Mai, Juni, Juli sind abgeschlossen - drei Perioden zu je 100.</summary>
    [Fact]
    public async Task As_far_back_as_possible_adds_up_every_completed_period_since_the_start()
    {
        Assert.Equal(300m, await CarryInAsync(carryOverStart: null, carryOverFrom: null));
    }

    /// <summary>
    /// „Diese Periode" ignoriert die Historie vollstaendig. Genau das ist der Fall, fuer den es den
    /// Modus gibt: ein Budget, das schon lange laeuft, soll ohne Altlast neu anfangen.
    /// </summary>
    [Fact]
    public async Task This_period_ignores_every_earlier_period()
    {
        Assert.Equal(0m, await CarryInAsync("this-period", null));
    }

    /// <summary>Ein eigenes Datum: nur noch Juli ist davor abgeschlossen.</summary>
    [Fact]
    public async Task A_chosen_start_date_counts_only_the_periods_from_that_date_on()
    {
        Assert.Equal(100m, await CarryInAsync("from-date", new DateOnly(2026, 7, 1)));
    }

    /// <summary>
    /// Ein Datum MITTEN in einer Periode darf die Periode nicht halbieren. Der 14. Juli gehoert zur
    /// Juli-Periode, also zaehlt der ganze Juli - sonst entstuende ein halber Uebertrag, den es in
    /// keiner Abrechnung gibt.
    /// </summary>
    [Fact]
    public async Task A_date_inside_a_period_takes_the_whole_period_not_a_part_of_it()
    {
        Assert.Equal(100m, await CarryInAsync("from-date", new DateOnly(2026, 7, 14)));
    }

    /// <summary>
    /// "from-date" ohne Datum ist kein Sonderfall, sondern eine unvollstaendige Angabe - sie faellt
    /// auf das bisherige Verhalten zurueck und erfindet kein Startdatum.
    /// </summary>
    [Fact]
    public async Task From_date_without_a_date_falls_back_to_the_budget_start()
    {
        Assert.Equal(300m, await CarryInAsync("from-date", null));
    }

    /// <summary>
    /// Der eigentliche Zweck des Uebertrags - und die Stelle, an der er jahrelang verloren ging. Er
    /// wurde im Speicher gerechnet, aber die Route wird von <c>BudgetReconciliationService</c>
    /// bedient, und der kannte ihn nicht: 100 Budget, 300 Uebertrag, und der Benutzer sah trotzdem
    /// "100" und war bei 150 Ausgaben angeblich 50 im Minus. Ein gemeldeter Uebertrag, der in keiner
    /// Zahl daneben auftaucht, ist schlimmer als keiner.
    /// </summary>
    [Fact]
    public async Task The_carried_balance_actually_raises_the_budget_the_remainder_and_the_percentage()
    {
        var status = await StatusAsync(null, null, spend: (new DateOnly(2026, 8, 4), 150m));

        Assert.Equal(300m, status.GetProperty("carryIn").GetDecimal());
        // Grundbetrag und effektiver Betrag stehen beide da - sonst waere nicht nachvollziehbar,
        // warum das Budget in diesem Monat 400 ist.
        Assert.Equal(100m, status.GetProperty("baseBudgetAmount").GetDecimal());
        Assert.Equal(400m, status.GetProperty("budgetAmount").GetDecimal());
        Assert.Equal(150m, status.GetProperty("spent").GetDecimal());
        Assert.Equal(250m, status.GetProperty("remaining").GetDecimal());
        // 150 von 400, nicht von 100.
        Assert.Equal(37.50m, status.GetProperty("percentUsed").GetDecimal());
    }

    private static async Task<decimal> CarryInAsync(string? carryOverStart, DateOnly? carryOverFrom) =>
        (await StatusAsync(carryOverStart, carryOverFrom)).GetProperty("carryIn").GetDecimal();

    private static async Task<JsonElement> StatusAsync(
        string? carryOverStart, DateOnly? carryOverFrom, (DateOnly Date, decimal Amount)? spend = null)
    {
        using var factory = new BackendWebApplicationFactory();
        var space = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var budget = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var account = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Uebertrag", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connection,
                FullWorthSpaceId = space,
                Provider = "test",
                InstitutionName = "Testbank",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = space,
                BankConnectionId = connection,
                IdentificationHash = $"h-{account:N}",
                ProviderAccountId = $"a-{account:N}",
                InstitutionName = "Testbank",
                DisplayName = "Girokonto",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Budgets.Add(new Budget
            {
                Id = budget,
                FullWorthSpaceId = space,
                Name = "Uebertrag",
                CategoryId = null,
                Amount = 100m,
                Currency = "EUR",
                Period = "monthly",
                CarryOver = true,
                StartDate = Start,
                CarryOverStart = carryOverStart,
                CarryOverFrom = carryOverFrom
            });
            if (spend is { } expense)
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = account,
                    ExternalKey = $"C-{Guid.NewGuid():N}",
                    Amount = -expense.Amount,
                    Currency = "EUR",
                    BookingDate = expense.Date,
                    RawJson = "{}"
                });
            }
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/budgets/{budget:D}/status?fullWorthSpaceId={space:D}&asOf={AsOf}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", owner.ToString("D"));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
