using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Budgets;

/// <summary>
/// Ein Budget, zwei Bildschirme, dieselbe Zahl.
///
/// "Wie viel ist von diesem Budget noch uebrig?" wird in diesem Haus an zwei Stellen gerechnet:
/// <c>BudgetReconciliationService</c> beantwortet die Budgetseite (und zwar ueber eine Middleware,
/// die die eigentlich gemappten Handler ueberholt), <c>AnalyticsService</c> beantwortet die
/// Zukunfts-Timeline auf der Buchungsseite. Beide sind erreichbar, beide haben gruene Tests, und
/// nichts zwingt sie zu derselben Antwort.
///
/// Genau diese Konstellation hat hier schon zweimal etwas Falsches auf den Bildschirm gebracht - beim
/// Uebertrag eines Budgets und beim Abgleich eines Kaufs. Deshalb steht hier kein erwarteter Wert,
/// sondern ein Vergleich: derselbe Zustand, zwei Leser, dieselbe Zahl.
/// </summary>
public sealed class BudgetStatusAgreementTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public async Task The_budget_screen_and_the_timeline_report_the_same_remaining_amount()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);

        var screen = await BudgetScreenAsync(factory, world);
        var timeline = await TimelineAsync(factory, world);

        Assert.True(screen.ContainsKey(world.Budget), "Die Budgetseite kennt das Budget nicht.");
        Assert.True(timeline.ContainsKey(world.Budget), "Die Zukunfts-Timeline zeigt das Budget nicht.");
        Assert.Equal(screen[world.Budget], timeline[world.Budget]);
    }

    [Fact]
    public async Task They_still_agree_when_a_split_moves_part_of_another_booking_into_the_budget()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, withSplit: true);

        var screen = await BudgetScreenAsync(factory, world);
        var timeline = await TimelineAsync(factory, world);

        // Der interessante Fall: die Buchung selbst zaehlt zu einer ANDEREN Kategorie, nur ein Teil
        // davon ist dem Budget zugeteilt. Genau fuer solche Aufteilungen - und fuer Erstattungen,
        // Umbuchungen und Fremdwaehrung - wurde die Middleware gebaut, die die Budgetseite bedient.
        // Der andere Leser ist einer der Handler, die sie ueberholt.
        Assert.Equal(screen[world.Budget], timeline[world.Budget]);
    }

    /// <summary>
    /// <c>GET /api/analytics/budget-status</c>: was die Budgetseite zeigt. Beantwortet wird das von
    /// <c>BudgetReconciliationCompatibilityMiddleware</c>, nicht vom gemappten Handler.
    /// </summary>
    private static async Task<Dictionary<Guid, decimal>> BudgetScreenAsync(
        BackendWebApplicationFactory factory, World world)
    {
        using var client = factory.CreateClient();
        using var request = Request($"/api/analytics/budget-status?fullWorthSpaceId={world.Space:D}", world.Owner);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
            item => item.GetProperty("id").GetGuid(),
            item => item.GetProperty("remaining").GetDecimal());
    }

    /// <summary>
    /// <c>GET /api/transactions/forecast</c>: was die Buchungsseite am Periodenende anschreibt. Der
    /// Betrag einer <c>budget-period</c>-Zeile ist der Rest desselben Budgets (#139).
    /// </summary>
    private static async Task<Dictionary<Guid, decimal>> TimelineAsync(
        BackendWebApplicationFactory factory, World world)
    {
        var periodEnd = new DateOnly(Today.Year, Today.Month, DateTime.DaysInMonth(Today.Year, Today.Month));
        var horizon = Math.Max(1, periodEnd.DayNumber - Today.DayNumber);
        using var client = factory.CreateClient();
        using var request = Request(
            $"/api/transactions/forecast?fullWorthSpaceId={world.Space:D}&horizonDays={horizon}", world.Owner);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "budget-period")
            .ToDictionary(
                item => item.GetProperty("sourceId").GetGuid(),
                item => item.GetProperty("amount").GetDecimal());
    }

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record World(
        Guid Owner, Guid Space, Guid Connection, Guid Account, Guid Category, Guid OtherCategory, Guid Budget);

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory, bool withSplit = false)
    {
        var world = new World(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM",
                DisplayName = "Budget owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Budget agreement", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space, UserId = world.Owner, Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = world.Connection, FullWorthSpaceId = world.Space, Provider = "test",
                InstitutionName = "Bank", Country = "DE",
                ProviderSessionId = $"b-{world.Connection:N}", Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = world.Account, FullWorthSpaceId = world.Space, BankConnectionId = world.Connection,
                Provider = "test", IdentificationHash = $"b-{world.Account:N}",
                ProviderAccountId = $"provider-{world.Account:N}", InstitutionName = "Bank",
                DisplayName = "Account", Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = world.Account, UserId = world.Owner, OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Categories.AddRange(
                new FinanceCategory
                {
                    Id = world.Category, FullWorthSpaceId = world.Space,
                    Key = $"b-{world.Category:N}", Name = "Groceries"
                },
                new FinanceCategory
                {
                    Id = world.OtherCategory, FullWorthSpaceId = world.Space,
                    Key = $"b-o-{world.OtherCategory:N}", Name = "Transport"
                });
            db.Budgets.Add(new Budget
            {
                Id = world.Budget, FullWorthSpaceId = world.Space, Name = "Groceries budget",
                CategoryId = world.Category, Amount = 500m, Currency = "EUR", Period = "monthly"
            });

            // Im laufenden Monat, damit BEIDE Leser dieselbe Periode meinen.
            var first = new DateOnly(Today.Year, Today.Month, 1);
            Spend(db, world, -100m, first);
            Spend(db, world, -50m, first.AddDays(Math.Min(5, Today.Day - 1)));

            if (withSplit)
            {
                var split = new FinanceTransaction
                {
                    AccountId = world.Account,
                    CategoryId = world.OtherCategory,
                    ExternalKey = $"b-split-{Guid.NewGuid():N}",
                    Amount = -200m,
                    Currency = "EUR",
                    BookingDate = first,
                    RawJson = "{}"
                };
                db.Transactions.Add(split);
                db.TransactionAllocations.AddRange(
                    new TransactionAllocation { TransactionId = split.Id, CategoryId = world.Category, Amount = -80m, Note = "Anteil Lebensmittel" },
                    new TransactionAllocation { TransactionId = split.Id, CategoryId = world.OtherCategory, Amount = -120m, Note = "Anteil Transport" });
            }

            await db.SaveChangesAsync();
        });

        return world;
    }

    private static void Spend(FullWorth.Backend.Data.FullWorthDbContext db, World world, decimal amount, DateOnly date) =>
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = world.Account,
            CategoryId = world.Category,
            ExternalKey = $"b-{Guid.NewGuid():N}",
            Amount = amount,
            Currency = "EUR",
            BookingDate = date,
            RawJson = "{}"
        });
}
