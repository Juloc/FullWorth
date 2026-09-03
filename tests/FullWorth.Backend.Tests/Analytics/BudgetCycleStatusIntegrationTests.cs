using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Analytics;

// §12.2: /api/analytics/budget-status must show EVERY active budget regardless of cycle type — not
// just "monthly" ones — with each budget resolving its OWN current window.
public sealed class BudgetCycleStatusIntegrationTests
{
    [Fact]
    public async Task NonMonthlyBudgetAppearsWithItsOwnWindowAndOnlyCountsSpendInsideIt()
    {
        using var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Weekly", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Weekly Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection { Id = connectionId, FullWorthSpaceId = spaceId, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = "weekly-session", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "test",
                IdentificationHash = "weekly-acct", ProviderAccountId = "weekly-acct", InstitutionName = "Bank",
                DisplayName = "Account", Currency = "EUR", IsActive = true, IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });

            // A 7-day custom window anchored on 2026-06-01, so the window for a June-1 reference is
            // exactly [2026-06-01, 2026-06-07].
            db.Budgets.Add(new Budget
            {
                FullWorthSpaceId = spaceId,
                Name = "Groceries (weekly)",
                Amount = 100m,
                Currency = "EUR",
                Period = "weekly",
                StartDate = new DateOnly(2026, 6, 1),
                IsActive = true
            });

            db.Transactions.AddRange(
                new FinanceTransaction { AccountId = accountId, ExternalKey = "in-window", Amount = -30m, Currency = "EUR", BookingDate = new DateOnly(2026, 6, 3) },
                new FinanceTransaction { AccountId = accountId, ExternalKey = "next-window", Amount = -999m, Currency = "EUR", BookingDate = new DateOnly(2026, 6, 10) });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/analytics/budget-status?fullWorthSpaceId={spaceId}&year=2026&month=6&currency=EUR");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("weekly", item.GetProperty("period").GetString());
        Assert.Equal("2026-06-01", item.GetProperty("periodStart").GetString());
        Assert.Equal("2026-06-07", item.GetProperty("periodEnd").GetString());
        Assert.Equal(30m, item.GetProperty("spent").GetDecimal());       // only the in-window transaction
        Assert.Equal(70m, item.GetProperty("remaining").GetDecimal());
    }
}
