using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts;

/// <summary>
/// A contract can say whether it counts as a fixed cost.
///
/// Before the flag, every consumer that turned contracts into a cost figure — the cashflow's "what is
/// available" and the reconciliation report — treated <b>every active contract</b> as a fixed cost, and
/// the only lever was to deactivate it, which also removes it from the list where it belongs. So a
/// recurring contract that is really a savings plan, or whose payment is already counted elsewhere,
/// silently reduced the money the owner was told was available.
///
/// These pin the three things that make the flag trustworthy: the default moves nobody's figure,
/// unticking it changes the figure and nothing else, and a save that does not mention the flag does not
/// reset it.
/// </summary>
public sealed class ContractFixedCostFlagTests
{
    private static readonly DateOnly AsOf = new(2026, 8, 20);

    /// <summary>
    /// The migration backfills TRUE, because that is exactly what every consumer assumed before the
    /// column existed. Nobody's cashflow figure may move the moment it runs.
    /// </summary>
    [Fact]
    public async Task A_contract_counts_as_a_fixed_cost_unless_it_says_otherwise()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, accountId, contractId) = await SeedAsync(factory, countsAsFixedCost: true);

        var contract = await GetContractAsync(factory, userId, contractId);
        Assert.True(contract.GetProperty("countsAsFixedCost").GetBoolean());

        var cashflow = await GetCashflowAsync(factory, userId);
        Assert.Equal(60m, cashflow.GetProperty("expectedFixedCosts").GetDecimal());
        _ = accountId;
    }

    /// <summary>
    /// Unticking it takes the amount out of the cost figure and gives it back to what is available — and
    /// does nothing else: the contract is still there, still active, still due, still 60 €.
    /// </summary>
    [Fact]
    public async Task A_contract_that_does_not_count_is_excluded_from_the_cost_figure_but_not_from_the_list()
    {
        using var counting = new BackendWebApplicationFactory();
        var counted = await SeedAsync(counting, countsAsFixedCost: true);
        var countedCashflow = await GetCashflowAsync(counting, counted.UserId);

        using var factory = new BackendWebApplicationFactory();
        var (userId, _, contractId) = await SeedAsync(factory, countsAsFixedCost: false);
        var cashflow = await GetCashflowAsync(factory, userId);

        Assert.Equal(0m, cashflow.GetProperty("expectedFixedCosts").GetDecimal());

        // The 60 really came back to the available figure rather than disappearing from the arithmetic.
        Assert.Equal(
            countedCashflow.GetProperty("available").GetDecimal() + 60m,
            cashflow.GetProperty("available").GetDecimal());

        // And the contract is not hidden, archived or stripped of its due date. The only thing that
        // changed is whether it is subtracted.
        var contract = await GetContractAsync(factory, userId, contractId);
        Assert.False(contract.GetProperty("countsAsFixedCost").GetBoolean());
        Assert.True(contract.GetProperty("isActive").GetBoolean());
        Assert.Equal("2026-08-25", contract.GetProperty("nextDueDate").GetString());
        Assert.Equal(60m, contract.GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// The write DTO carries the flag as a nullable, so a client that does not send it leaves the stored
    /// value alone. A plain bool would reset it to the default on every save, quietly turning a contract
    /// the owner had excluded back into a fixed cost — the kind of regression nobody reports, because
    /// the number only looks slightly wrong.
    /// </summary>
    [Fact]
    public async Task A_save_that_does_not_mention_the_flag_leaves_it_alone()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, accountId, contractId) = await SeedAsync(factory, countsAsFixedCost: false);

        // Exactly the payload an older client sends: no countsAsFixedCost at all.
        var request = UserRequest(
            HttpMethod.Put,
            $"/api/contracts/{contractId:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId);
        request.Content = JsonContent.Create(new
        {
            name = "Internet",
            kind = "contract",
            accountId,
            amount = 60m,
            currency = "EUR",
            billingCycle = "monthly",
            interval = 1,
            nextDueDate = "2026-08-25",
            isActive = true
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contract = await GetContractAsync(factory, userId, contractId);
        Assert.False(contract.GetProperty("countsAsFixedCost").GetBoolean());
        Assert.Equal(0m, (await GetCashflowAsync(factory, userId)).GetProperty("expectedFixedCosts").GetDecimal());
    }

    // ---- fixtures ----

    private static async Task<(Guid UserId, Guid AccountId, Guid ContractId)> SeedAsync(
        BackendWebApplicationFactory factory, bool countsAsFixedCost)
    {
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Fixed cost user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"fixedcost-{accountId:N}",
                ProviderAccountId = $"fixedcost-{accountId:N}",
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
                Amount = 60m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                NextDueDate = new DateOnly(2026, 8, 25),
                IsActive = true,
                CountsAsFixedCost = countsAsFixedCost
            });
            await db.SaveChangesAsync();
        });

        return (userId, accountId, contractId);
    }

    private static async Task<JsonElement> GetCashflowAsync(BackendWebApplicationFactory factory, Guid userId)
    {
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/cashflow/available?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf={AsOf:yyyy-MM-dd}",
            userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> GetContractAsync(
        BackendWebApplicationFactory factory, Guid userId, Guid contractId)
    {
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/contracts/{contractId:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
