using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts;

// UI_UX_SPEC §13 contract detail activity: GET /api/contracts/{id}/activity links payments to a
// contract, and computes next-expected + annualized cost server-side (§30). These lock the linking
// heuristic (name/provider match, currency + transfer/ignored filters) and the authorization scope.
public sealed class ContractActivityTests
{
    [Fact]
    public async Task Activity_LinksMatchingPaymentsAndComputesAnnualizedAndNextExpected()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/contracts/{s.Contract}/activity?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activity = await response.Content.ReadFromJsonAsync<ActivityView>();
        Assert.NotNull(activity);

        Assert.Equal("manual", activity!.ValueMode);          // not auto-detected
        Assert.Equal(3, activity.MatchedCount);                // two normalized matches + one provider-contains match
        Assert.Equal(3, activity.Payments.Count);
        Assert.Equal(9.99m, activity.AverageAmount);
        Assert.Equal(119.88m, activity.AnnualizedAmount);      // 9.99 * 12 (monthly, interval 1)
        Assert.Equal(new DateOnly(2030, 3, 15), activity.NextExpected); // future NextDueDate returned verbatim
        Assert.Equal(new DateOnly(2026, 8, 5), activity.LastPayment);   // latest matched booking date
        Assert.All(activity.Payments, p => Assert.Equal(9.99m, p.Amount));
    }

    [Fact]
    public async Task ListExposesNormalizedMonthlyAndAnnualCosts()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={s.Space}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var contract = Assert.Single(json.RootElement.EnumerateArray().ToList());

        Assert.Equal(9.99m, contract.GetProperty("monthlyEquivalent").GetDecimal());
        Assert.Equal(119.88m, contract.GetProperty("annualizedAmount").GetDecimal());
    }

    [Fact]
    public async Task Activity_NonMemberGetsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/contracts/{s.Contract}/activity?fullWorthSpaceId={s.Space}", s.Outsider));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            foreach (var id in new[] { s.Owner, s.Outsider })
                db.Users.Add(new FullWorthUser { Id = id, EmailNormalized = $"{id:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = $"Act {id:N}", IsActive = true });

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Activity", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner });

            db.BankConnections.Add(new BankConnection { Id = s.Connection, FullWorthSpaceId = s.Space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"act-{s.Connection:N}", Status = "AUTHORIZED" });
            db.Accounts.Add(new FinanceAccount { Id = s.Account, FullWorthSpaceId = s.Space, BankConnectionId = s.Connection, Provider = "test", IdentificationHash = $"act-{s.Account:N}", ProviderAccountId = $"prov-{s.Account:N}", InstitutionName = "Bank", DisplayName = "Account", Currency = "EUR" });
            db.AccountOwners.Add(new AccountOwner { AccountId = s.Account, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });

            db.Contracts.Add(new RecurringContract
            {
                Id = s.Contract, FullWorthSpaceId = s.Space, Name = "Spotify", ProviderName = "Spotify AB",
                Kind = "subscription", Amount = 9.99m, Currency = "EUR", BillingCycle = "monthly", Interval = 1,
                NextDueDate = new DateOnly(2030, 3, 15), AutoDetected = false, IsActive = true
            });

            void Tx(string key, decimal amount, string? counterparty, string? normalized, DateOnly date, string currency = "EUR", bool transfer = false, bool ignored = false) =>
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = s.Account, ExternalKey = key, Amount = amount, Currency = currency,
                    Counterparty = counterparty, NormalizedCounterparty = normalized, BookingDate = date,
                    IsTransfer = transfer, IsIgnored = ignored, CategorizationSource = "none"
                });

            Tx("m1", -9.99m, "SPOTIFY", "spotify", new DateOnly(2026, 6, 5));                    // normalized == name
            Tx("m2", -9.99m, "SPOTIFY", "spotify", new DateOnly(2026, 7, 5));                    // normalized == name
            Tx("m3", -9.99m, "SPOTIFY AB STOCKHOLM", "spotify ab stockholm", new DateOnly(2026, 8, 5)); // counterparty contains provider
            Tx("n1", -12.99m, "NETFLIX", "netflix", new DateOnly(2026, 8, 6));                   // no match
            Tx("n2", -9.99m, "SPOTIFY", "spotify", new DateOnly(2026, 8, 7), currency: "USD");   // wrong currency
            Tx("n3", -9.99m, "SPOTIFY", "spotify", new DateOnly(2026, 8, 8), transfer: true);    // transfer excluded

            await db.SaveChangesAsync();
        });
        return s;
    }

    private sealed record Scenario(Guid Owner, Guid Outsider, Guid Space, Guid Connection, Guid Account, Guid Contract);

    private sealed record ActivityView(
        Guid ContractId, string ValueMode, decimal ExpectedAmount, string Currency, decimal AnnualizedAmount,
        DateOnly? NextExpected, DateOnly? LastPayment, int MatchedCount, decimal? AverageAmount, List<PaymentView> Payments);

    private sealed record PaymentView(Guid Id, DateOnly? Date, decimal Amount, string Currency);
}
