using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// The basis of the forward preview on the wealth page.
///
/// The preview used to take its savings rate from the measured net-worth curve — last value minus first,
/// divided by the months. That is backwards twice over: it extrapolates from the past, and it bundles
/// market movement in with actual saving, so a good year on the markets read as a high savings rate and
/// then compounded on top of itself.
///
/// The replacement composes the figure forward from things that exist: configured income, the contracts
/// marked as fixed costs, and what is actually spent besides those. Which creates exactly one trap, and
/// it is the reason this file exists — see the double-count test.
/// </summary>
public sealed class WealthPreviewBasisTests
{
    /// <summary>
    /// **The trap.** A contract is a fixed cost, and its payment also shows up as a booked expense. Add
    /// both and the same rent is subtracted twice — the surplus then looks 60 € worse every month than
    /// it is, and nobody would spot it, because both numbers are individually right.
    ///
    /// The cashflow endpoint can add both because it compares future dues against past spending, which
    /// are different periods. A monthly steady-state figure cannot.
    /// </summary>
    [Fact]
    public async Task A_contract_payment_is_a_fixed_cost_and_not_also_variable_spend()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, monthlyIncome: 3_000m, contractAmount: 60m, linkContractPayment: true);

        var basis = await GetAsync(factory, scenario);

        Assert.Equal(3_000m, basis.GetProperty("monthlyIncome").GetDecimal());
        Assert.Equal(60m, basis.GetProperty("monthlyFixedCosts").GetDecimal());
        // The 60 payment is linked, so it is NOT in the variable average. Only the 120 groceries are,
        // spread over the six-month window.
        Assert.Equal(20m, basis.GetProperty("monthlyVariableSpend").GetDecimal());
        Assert.Equal(2_920m, basis.GetProperty("monthlySurplus").GetDecimal());
    }

    /// <summary>
    /// The same books without the link: now the payment is just an expense, and the contract is
    /// <b>still</b> a fixed cost, so it is counted twice. That is not a bug in this endpoint but the
    /// state of the data — an unlinked contract payment is indistinguishable from any other expense.
    /// The test exists to make the difference visible and to prove the link is what does the work.
    /// </summary>
    [Fact]
    public async Task Without_the_link_the_same_payment_is_counted_as_both_which_is_what_the_link_prevents()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, monthlyIncome: 3_000m, contractAmount: 60m, linkContractPayment: false);

        var basis = await GetAsync(factory, scenario);

        Assert.Equal(60m, basis.GetProperty("monthlyFixedCosts").GetDecimal());
        // (120 groceries + 60 payment) / 6 months = 30.
        Assert.Equal(30m, basis.GetProperty("monthlyVariableSpend").GetDecimal());
        Assert.Equal(2_910m, basis.GetProperty("monthlySurplus").GetDecimal());
    }

    /// <summary>
    /// A contract the owner took out of the fixed costs is not in the basis either — otherwise the flag
    /// would mean one thing in the cashflow and another in the preview.
    /// </summary>
    [Fact]
    public async Task A_contract_that_does_not_count_as_a_fixed_cost_is_not_in_the_basis()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(
            factory, monthlyIncome: 3_000m, contractAmount: 60m, linkContractPayment: true, countsAsFixedCost: false);

        var basis = await GetAsync(factory, scenario);

        Assert.Equal(0m, basis.GetProperty("monthlyFixedCosts").GetDecimal());
        Assert.DoesNotContain(
            basis.GetProperty("lines").EnumerateArray(),
            line => line.GetProperty("kind").GetString() == "fixed");
        // And it did not quietly reappear as variable spend: its payment is still linked to it.
        Assert.Equal(20m, basis.GetProperty("monthlyVariableSpend").GetDecimal());
    }

    /// <summary>
    /// Budget limits are reported so the screen can put them next to the actual average, and they are
    /// never part of the arithmetic. A budget is an intention; the preview is about what is likely.
    /// </summary>
    [Fact]
    public async Task Budget_limits_are_reported_beside_the_average_and_never_inside_it()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(
            factory, monthlyIncome: 3_000m, contractAmount: 60m, linkContractPayment: true, budgetLimit: 400m);

        var basis = await GetAsync(factory, scenario);

        Assert.Equal(400m, basis.GetProperty("monthlyBudgetLimit").GetDecimal());
        // Unchanged by the budget: still income - fixed - the observed average.
        Assert.Equal(20m, basis.GetProperty("monthlyVariableSpend").GetDecimal());
        Assert.Equal(2_920m, basis.GetProperty("monthlySurplus").GetDecimal());
    }

    /// <summary>
    /// Every line is positive and carries its sign in its kind, so a caller cannot add an expense to
    /// income by accident — and the variable line says it is an estimate, because nobody entered it.
    /// </summary>
    [Fact]
    public async Task Every_line_is_positive_and_the_observed_one_says_so()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, monthlyIncome: 3_000m, contractAmount: 60m, linkContractPayment: true);

        var basis = await GetAsync(factory, scenario);
        var lines = basis.GetProperty("lines").EnumerateArray().ToList();

        Assert.All(lines, line => Assert.True(line.GetProperty("monthlyAmount").GetDecimal() > 0m));
        Assert.All(lines, line => Assert.Contains(
            line.GetProperty("kind").GetString(), new[] { "income", "fixed", "variable" }));

        var variable = Assert.Single(lines, line => line.GetProperty("kind").GetString() == "variable");
        Assert.True(variable.GetProperty("isEstimate").GetBoolean());
        Assert.True(variable.GetProperty("id").ValueKind == JsonValueKind.Null);

        var income = Assert.Single(lines, line => line.GetProperty("kind").GetString() == "income");
        Assert.False(income.GetProperty("isEstimate").GetBoolean());
        Assert.Equal(6, basis.GetProperty("observedMonths").GetInt32());
    }

    // ---- fixtures ----

    private sealed record Scenario(Guid UserId, Guid AccountId, Guid ContractId);

    /// <summary>
    /// Six whole months before this one, so the window the endpoint uses is fully covered: 120 € of
    /// groceries and one 60 € contract payment, all inside it.
    /// </summary>
    private static async Task<Scenario> SeedAsync(
        BackendWebApplicationFactory factory,
        decimal monthlyIncome,
        decimal contractAmount,
        bool linkContractPayment,
        bool countsAsFixedCost = true,
        decimal? budgetLimit = null)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var groceriesId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var insideWindow = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Preview user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"preview-{accountId:N}",
                ProviderAccountId = $"preview-{accountId:N}",
                InstitutionName = "Test Bank",
                DisplayName = "Primary",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Contracts.Add(new RecurringContract
            {
                Id = contractId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Internet",
                Kind = "contract",
                AccountId = accountId,
                Amount = contractAmount,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                NextDueDate = today.AddDays(5),
                IsActive = true,
                CountsAsFixedCost = countsAsFixedCost
            });
            db.Transactions.AddRange(
                Expense(groceriesId, accountId, -120m, insideWindow, "Groceries"),
                Expense(paymentId, accountId, -contractAmount, insideWindow, "Internet payment"));
            if (budgetLimit is { } limit)
                db.Budgets.Add(new Modules.Budgets.Budget
                {
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Name = "Everyday",
                    Amount = limit,
                    Currency = "EUR",
                    Period = "monthly",
                    IsActive = true
                });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "IncomeSchedules"
("Id","FullWorthSpaceId","Name","AccountId","NormalizedCounterparty","ExpectedAmount","Currency","Cycle","Interval","AnchorDate","NextExpectedDate","ValueMode","AutoDetected","IsActive","CreatedAt","UpdatedAt")
VALUES ({scheduleId},{FullWorthSpaceDefaults.LegacyId},{"Salary"},{accountId},{"employer"},{monthlyIncome},{"EUR"},{"monthly"},{1},{insideWindow},{today.AddDays(10)},{"fixed"},{false},{true},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow})
""");

            if (linkContractPayment)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks"
("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","CreatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{contractId},{paymentId},{contractAmount},{"manual"},{DateTimeOffset.UtcNow})
""");
        });

        return new Scenario(userId, accountId, contractId);
    }

    private static FinanceTransaction Expense(Guid id, Guid accountId, decimal amount, DateOnly date, string counterparty) =>
        new()
        {
            Id = id,
            AccountId = accountId,
            Amount = amount,
            Currency = "EUR",
            BookingDate = date,
            ValueDate = date,
            Counterparty = counterparty,
            // Unique per transaction: IX_Transactions_AccountId_ExternalKey is unique, so two seeded rows
            // on one account with the same (missing) key collide.
            ExternalKey = $"preview:{id:N}",
            Status = "BOOK"
        };

    private static async Task<JsonElement> GetAsync(BackendWebApplicationFactory factory, Scenario scenario)
    {
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/wealth/preview-basis?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&months=6");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.UserId.ToString("D"));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
