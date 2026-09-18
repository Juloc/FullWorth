using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Analytics;

/// <summary>
/// #139: <c>GET /api/transactions/forecast</c> - expected contract payments, expected incomes and
/// budget periods for the account/group/all-accounts scope, deduplicated against whatever is already
/// booked or pending so a forecast never doubles up with a real transaction.
/// </summary>
public sealed class ForecastTimelineTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    [Fact]
    public async Task ContractWithNoHistoryAppearsOnceWithCorrectFields()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, accountId) = await SeedSpaceAsync(factory);
        var contractId = Guid.NewGuid();
        var dueDate = Today.AddDays(5);

        await factory.SeedAsync(async db =>
        {
            db.Contracts.Add(new RecurringContract
            {
                Id = contractId,
                FullWorthSpaceId = spaceId,
                Name = "Vodafone",
                AccountId = accountId,
                Amount = 39.99m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                NextDueDate = dueDate,
                IsActive = true
            });
            await db.SaveChangesAsync();
        });

        // A short horizon that reaches the first occurrence but not the second (dueDate + one month).
        using var json = await GetForecastAsync(factory, userId, spaceId, horizonDays: 20);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var entry = Assert.Single(items, item => item.GetProperty("sourceId").GetGuid() == contractId);
        Assert.Equal("contract", entry.GetProperty("kind").GetString());
        Assert.Equal(dueDate.ToString("yyyy-MM-dd"), entry.GetProperty("date").GetString());
        Assert.Equal(-39.99m, entry.GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", entry.GetProperty("currency").GetString());
        Assert.False(entry.GetProperty("isEstimate").GetBoolean());
    }

    /// <summary>The nearest due date already has a matching (linked) transaction - it must not appear a
    /// second time as a forecast row, but the occurrence two cycles out is still in the future and
    /// still shows up.</summary>
    [Fact]
    public async Task NearestOccurrenceAlreadyLinkedIsSuppressedButLaterOccurrenceAppears()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, accountId) = await SeedSpaceAsync(factory);
        var contractId = Guid.NewGuid();
        var nearestDue = Today.AddDays(2);
        var paymentId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Contracts.Add(new RecurringContract
            {
                Id = contractId,
                FullWorthSpaceId = spaceId,
                Name = "Internet",
                AccountId = accountId,
                Amount = 60m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                NextDueDate = nearestDue,
                IsActive = true
            });
            db.Transactions.Add(Tx(paymentId, accountId, -60m, nearestDue, "Internet payment"));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","CreatedAt")
VALUES ({Guid.NewGuid()},{spaceId},{contractId},{paymentId},{60m},{"manual"},{DateTimeOffset.UtcNow})
""");
        });

        // Wide enough to reach the second occurrence (nearestDue + 1 month) but not the third.
        using var json = await GetForecastAsync(factory, userId, spaceId, horizonDays: 40);
        var items = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("sourceId").GetGuid() == contractId).ToArray();

        Assert.Single(items);
        Assert.Equal(nearestDue.AddMonths(1).ToString("yyyy-MM-dd"), items[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task IncomeScheduleMatchingPendingTransactionIsSuppressed()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, accountId) = await SeedSpaceAsync(factory);
        var nextDate = Today.AddDays(3);

        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(Tx(Guid.NewGuid(), accountId, 2450m, nextDate, "Employer GmbH", status: "PDNG"));
            await db.SaveChangesAsync();
        });
        await SeedIncomeScheduleAsync(factory, spaceId, accountId, "Salary", "EMPLOYER GMBH", 2450m, nextDate, "monthly", "fixed");

        using var json = await GetForecastAsync(factory, userId, spaceId);
        var items = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "income").ToArray();

        Assert.Empty(items.Where(item => item.GetProperty("date").GetString() == nextDate.ToString("yyyy-MM-dd")));
    }

    [Fact]
    public async Task IncomeScheduleNotMatchingAnyPendingTransactionAppears()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, accountId) = await SeedSpaceAsync(factory);
        var nextDate = Today.AddDays(3);

        await SeedIncomeScheduleAsync(factory, spaceId, accountId, "Salary", "EMPLOYER GMBH", 2450m, nextDate, "monthly", "fixed");

        using var json = await GetForecastAsync(factory, userId, spaceId, horizonDays: 20);
        var items = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "income").ToArray();

        var entry = Assert.Single(items);
        Assert.Equal(nextDate.ToString("yyyy-MM-dd"), entry.GetProperty("date").GetString());
        Assert.Equal(2450m, entry.GetProperty("amount").GetDecimal());
        Assert.False(entry.GetProperty("isEstimate").GetBoolean());
    }

    [Fact]
    public async Task IncomeScheduleWithoutExpectedAmountAppearsAsEstimateWithNullAmount()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, accountId) = await SeedSpaceAsync(factory);
        var nextDate = Today.AddDays(4);

        await SeedIncomeScheduleAsync(factory, spaceId, accountId, "Freelance", "CLIENT AG", null, nextDate, "monthly", "average");

        using var json = await GetForecastAsync(factory, userId, spaceId, horizonDays: 20);
        var entry = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "income"));

        Assert.Equal(JsonValueKind.Null, entry.GetProperty("amount").ValueKind);
        Assert.True(entry.GetProperty("isEstimate").GetBoolean());
    }

    [Fact]
    public async Task ContractWithVaryingLinkedAmountsIsMarkedEstimateAndIdenticalOneIsNot()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, accountId) = await SeedSpaceAsync(factory);
        var varyingContractId = Guid.NewGuid();
        var stableContractId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Contracts.AddRange(
                new RecurringContract { Id = varyingContractId, FullWorthSpaceId = spaceId, Name = "Electricity", AccountId = accountId, Amount = 80m, Currency = "EUR", BillingCycle = "monthly", Interval = 1, NextDueDate = Today.AddDays(10), IsActive = true },
                new RecurringContract { Id = stableContractId, FullWorthSpaceId = spaceId, Name = "Gym", AccountId = accountId, Amount = 30m, Currency = "EUR", BillingCycle = "monthly", Interval = 1, NextDueDate = Today.AddDays(11), IsActive = true });

            var varying = new[] { Tx(Guid.NewGuid(), accountId, -70m, Today.AddMonths(-1), "Electricity"), Tx(Guid.NewGuid(), accountId, -90m, Today.AddMonths(-2), "Electricity"), Tx(Guid.NewGuid(), accountId, -75m, Today.AddMonths(-3), "Electricity") };
            var stable = new[] { Tx(Guid.NewGuid(), accountId, -30m, Today.AddMonths(-1), "Gym"), Tx(Guid.NewGuid(), accountId, -30m, Today.AddMonths(-2), "Gym"), Tx(Guid.NewGuid(), accountId, -30m, Today.AddMonths(-3), "Gym") };
            db.Transactions.AddRange(varying);
            db.Transactions.AddRange(stable);
            await db.SaveChangesAsync();

            foreach (var tx in varying)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","CreatedAt")
VALUES ({Guid.NewGuid()},{spaceId},{varyingContractId},{tx.Id},{Math.Abs(tx.Amount)},{"manual"},{DateTimeOffset.UtcNow})
""");
            foreach (var tx in stable)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","CreatedAt")
VALUES ({Guid.NewGuid()},{spaceId},{stableContractId},{tx.Id},{Math.Abs(tx.Amount)},{"manual"},{DateTimeOffset.UtcNow})
""");
        });

        using var json = await GetForecastAsync(factory, userId, spaceId, horizonDays: 20);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var varyingEntry = Assert.Single(items, item => item.GetProperty("sourceId").GetGuid() == varyingContractId);
        var stableEntry = Assert.Single(items, item => item.GetProperty("sourceId").GetGuid() == stableContractId);

        Assert.True(varyingEntry.GetProperty("isEstimate").GetBoolean());
        Assert.False(stableEntry.GetProperty("isEstimate").GetBoolean());
    }

    [Fact]
    public async Task BudgetPeriodInsideHorizonAppearsAndBeyondHorizonIsAbsent()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, _) = await SeedSpaceAsync(factory);

        await factory.SeedAsync(async db =>
        {
            db.Budgets.Add(new Budget
            {
                FullWorthSpaceId = spaceId,
                Name = "Groceries",
                Amount = 300m,
                Currency = "EUR",
                Period = "monthly",
                IsActive = true
            });
            await db.SaveChangesAsync();
        });

        // The current calendar month's period end, and exactly how many days from "today" that is - the
        // horizon that reaches it is chosen from that distance instead of a fixed guess, so the test does
        // not depend on which day of the month it happens to run on.
        var periodEnd = new DateOnly(Today.Year, Today.Month, DateTime.DaysInMonth(Today.Year, Today.Month));
        var remainingDays = periodEnd.DayNumber - Today.DayNumber;

        using var nearJson = await GetForecastAsync(factory, userId, spaceId, horizonDays: Math.Max(1, remainingDays));
        var nearItems = nearJson.RootElement.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("kind").GetString() == "budget-period").ToArray();
        Assert.Single(nearItems);
        Assert.Equal("Groceries", nearItems[0].GetProperty("label").GetString());
        Assert.Equal(periodEnd.ToString("yyyy-MM-dd"), nearItems[0].GetProperty("date").GetString());

        // One day short of the period end, the marker must not appear yet. Only meaningful when "today"
        // is not already the last day of the month (remainingDays == 0, where any horizon includes it).
        if (remainingDays >= 2)
        {
            using var shortJson = await GetForecastAsync(factory, userId, spaceId, horizonDays: remainingDays - 1);
            var shortItems = shortJson.RootElement.GetProperty("items").EnumerateArray()
                .Where(item => item.GetProperty("kind").GetString() == "budget-period").ToArray();
            Assert.Empty(shortItems);
        }
    }

    [Fact]
    public async Task AccountScopedRequestExcludesContractOnDifferentAccountAndHouseholdWideOnlyAppearsUnscoped()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, accountId) = await SeedSpaceAsync(factory);
        var otherAccountId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = otherAccountId, FullWorthSpaceId = spaceId, Provider = "manual",
                IdentificationHash = $"forecast-other-{otherAccountId:N}", ProviderAccountId = $"forecast-other-{otherAccountId:N}",
                InstitutionName = "Test Bank", DisplayName = "Other account", Currency = "EUR", IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = otherAccountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            await db.SaveChangesAsync();
        });

        var onOtherAccountId = Guid.NewGuid();
        var householdWideId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Contracts.AddRange(
                new RecurringContract { Id = onOtherAccountId, FullWorthSpaceId = spaceId, Name = "Other account contract", AccountId = otherAccountId, Amount = 10m, Currency = "EUR", BillingCycle = "monthly", Interval = 1, NextDueDate = Today.AddDays(5), IsActive = true },
                new RecurringContract { Id = householdWideId, FullWorthSpaceId = spaceId, Name = "Household contract", AccountId = null, Amount = 20m, Currency = "EUR", BillingCycle = "monthly", Interval = 1, NextDueDate = Today.AddDays(6), IsActive = true });
            await db.SaveChangesAsync();
        });

        using var scoped = await GetForecastAsync(factory, userId, spaceId, accountId: accountId);
        var scopedIds = scoped.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("sourceId").GetGuid()).ToHashSet();
        Assert.DoesNotContain(onOtherAccountId, scopedIds);
        Assert.DoesNotContain(householdWideId, scopedIds);

        using var unscoped = await GetForecastAsync(factory, userId, spaceId);
        var unscopedIds = unscoped.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("sourceId").GetGuid()).ToHashSet();
        Assert.Contains(householdWideId, unscopedIds);
        Assert.Contains(onOtherAccountId, unscopedIds);
    }

    [Fact]
    public async Task HorizonDaysClampsToOneHundredEightyAndAtLeastOne()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId, _) = await SeedSpaceAsync(factory);

        using var tooLarge = await GetForecastAsync(factory, userId, spaceId, horizonDays: 500);
        Assert.Equal(Today.AddDays(180).ToString("yyyy-MM-dd"), tooLarge.RootElement.GetProperty("to").GetString());

        using var tooSmall = await GetForecastAsync(factory, userId, spaceId, horizonDays: 0);
        Assert.Equal(Today.AddDays(1).ToString("yyyy-MM-dd"), tooSmall.RootElement.GetProperty("to").GetString());

        using var negative = await GetForecastAsync(factory, userId, spaceId, horizonDays: -10);
        Assert.Equal(Today.AddDays(1).ToString("yyyy-MM-dd"), negative.RootElement.GetProperty("to").GetString());
    }

    private static async Task<(Guid UserId, Guid SpaceId, Guid AccountId)> SeedSpaceAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Forecast user", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Forecast space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Member });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId, FullWorthSpaceId = spaceId, Provider = "manual",
                IdentificationHash = $"forecast-{accountId:N}", ProviderAccountId = $"forecast-{accountId:N}",
                InstitutionName = "Test Bank", DisplayName = "Account", Currency = "EUR", IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            await db.SaveChangesAsync();
        });
        return (userId, spaceId, accountId);
    }

    private static async Task SeedIncomeScheduleAsync(
        BackendWebApplicationFactory factory, Guid spaceId, Guid accountId, string name,
        string normalizedCounterparty, decimal? expectedAmount, DateOnly nextDate, string cycle, string valueMode)
    {
        await factory.SeedAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "IncomeSchedules"
("Id","FullWorthSpaceId","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{spaceId},{name},{accountId},{normalizedCounterparty},{expectedAmount},{"EUR"},{cycle},{1},{nextDate},{valueMode},{false},{true},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow})
""");
        });
    }

    private static FinanceTransaction Tx(Guid id, Guid accountId, decimal amount, DateOnly date, string label, string status = "BOOK") => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = $"forecast:{id:N}",
        Status = status,
        BookingDate = date,
        ValueDate = date,
        Amount = amount,
        Currency = "EUR",
        Counterparty = label,
        NormalizedCounterparty = label.ToUpperInvariant(),
        RawJson = "{}"
    };

    private static async Task<JsonDocument> GetForecastAsync(
        BackendWebApplicationFactory factory, Guid userId, Guid spaceId, Guid? accountId = null, Guid? groupId = null, int? horizonDays = null)
    {
        using var client = factory.CreateClient();
        var query = $"fullWorthSpaceId={spaceId}";
        if (accountId.HasValue) query += $"&accountId={accountId}";
        if (groupId.HasValue) query += $"&groupId={groupId}";
        if (horizonDays.HasValue) query += $"&horizonDays={horizonDays}";
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/transactions/forecast?{query}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
