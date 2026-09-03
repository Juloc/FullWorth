using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Contracts.PriceChanges;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Contracts.PriceChanges;

public sealed class PriceChangeIntegrationTests
{
    [Fact]
    public async Task DetectCreatesManualSuggestionWithEvidenceAndRefreshesAutomaticContract()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/price-changes/detect?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner,
            new PriceChangeDetectionRequest(new(2026, 8, 25))));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("autoRefreshedContracts").GetInt32());
        var suggestion = Assert.Single(json.RootElement.GetProperty("suggestions").EnumerateArray());
        Assert.Equal(scenario.ManualContract, suggestion.GetProperty("contractId").GetGuid());
        Assert.Equal(100m, suggestion.GetProperty("oldAmount").GetDecimal());
        Assert.Equal(106m, suggestion.GetProperty("newAmount").GetDecimal());
        Assert.Equal(6m, suggestion.GetProperty("percentChange").GetDecimal());
        Assert.Equal(scenario.ManualEvidence, suggestion.GetProperty("evidenceTransactionId").GetGuid());
        Assert.Equal("2026-08-05", suggestion.GetProperty("evidenceTransactionDate").GetString());
        Assert.Equal("pending", suggestion.GetProperty("status").GetString());

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(100m, await db.Contracts.Where(contract => contract.Id == scenario.ManualContract).Select(contract => contract.Amount).SingleAsync());
            Assert.Equal(60m, await db.Contracts.Where(contract => contract.Id == scenario.AutomaticContract).Select(contract => contract.Amount).SingleAsync());
            Assert.Equal(100m, await db.Contracts.Where(contract => contract.Id == scenario.UnderThresholdContract).Select(contract => contract.Amount).SingleAsync());
        });
    }

    [Fact]
    public async Task ConfiguredThresholdCanSuppressAnOtherwiseDetectedChange()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PriceChangeStore>();

        var outcome = await store.DetectAsync(scenario.Owner, scenario.SpaceA, new()
        {
            MinimumPercentChange = 21m,
            AutoRefreshPolicy = PriceChangeAutoRefreshPolicy.Disabled
        }, new(2026, 8, 25), CancellationToken.None);

        Assert.Equal(PriceChangeMutationResult.Success, outcome.Result);
        Assert.Empty(outcome.Suggestions!);
        Assert.Equal(0, outcome.AutoRefreshedContracts);
    }

    [Fact]
    public async Task ConfirmUpdatesManualContractAndIgnoreOnlyChangesSuggestionStatus()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var suggestionId = await DetectManualSuggestionAsync(client, scenario);

        using var confirmed = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/price-changes/{suggestionId}/confirm?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(106m, await db.Contracts.Where(contract => contract.Id == scenario.ManualContract).Select(contract => contract.Amount).SingleAsync());
            Assert.Equal(PriceChangeSuggestionStatuses.Confirmed, await db.PriceChangeSuggestions.Where(suggestion => suggestion.Id == suggestionId).Select(suggestion => suggestion.Status).SingleAsync());
        });

        using var secondFactory = new BackendWebApplicationFactory();
        var secondScenario = await SeedScenarioAsync(secondFactory);
        using var secondClient = secondFactory.CreateClient();
        var ignoredSuggestionId = await DetectManualSuggestionAsync(secondClient, secondScenario);

        using var ignored = await secondClient.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/price-changes/{ignoredSuggestionId}/ignore?fullWorthSpaceId={secondScenario.SpaceA}", secondScenario.Owner));
        Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);

        await secondFactory.SeedAsync(async db =>
        {
            Assert.Equal(100m, await db.Contracts.Where(contract => contract.Id == secondScenario.ManualContract).Select(contract => contract.Amount).SingleAsync());
            Assert.Equal(PriceChangeSuggestionStatuses.Ignored, await db.PriceChangeSuggestions.Where(suggestion => suggestion.Id == ignoredSuggestionId).Select(suggestion => suggestion.Status).SingleAsync());
        });
    }

    [Fact]
    public async Task ForeignAndNonOwnerPriceChangeActionsDoNotLeak()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();
        var suggestionId = await DetectManualSuggestionAsync(client, scenario);

        using var outsiderDetect = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/price-changes/detect?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside,
            new PriceChangeDetectionRequest(new(2026, 8, 25))));
        using var outsiderConfirm = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/price-changes/{suggestionId}/confirm?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));
        using var memberIgnore = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/price-changes/{suggestionId}/ignore?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));

        Assert.Equal(HttpStatusCode.NotFound, outsiderDetect.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderConfirm.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberIgnore.StatusCode);
    }

    private static async Task<Guid> DetectManualSuggestionAsync(HttpClient client, Scenario scenario)
    {
        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/price-changes/detect?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner,
            new PriceChangeDetectionRequest(new(2026, 8, 25))));
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Assert.Single(json.RootElement.GetProperty("suggestions").EnumerateArray()).GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
                db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = $"I5 {userId:N}", IsActive = true });

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.SpaceA, Name = "I5 Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member });
            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.Connection, FullWorthSpaceId = scenario.SpaceA, Provider = "test", InstitutionName = "I5 Bank", Country = "DE",
                ProviderSessionId = "i5-session", Status = "AUTHORIZED"
            });
            db.Accounts.AddRange(
                Account(scenario.ManualAccount, scenario.SpaceA, scenario.Connection, "Manual"),
                Account(scenario.UnderThresholdAccount, scenario.SpaceA, scenario.Connection, "Under threshold"),
                Account(scenario.AutomaticAccount, scenario.SpaceA, scenario.Connection, "Automatic"));
            db.AccountOwners.AddRange(
                Owner(scenario.ManualAccount, scenario.Owner), Owner(scenario.UnderThresholdAccount, scenario.Owner), Owner(scenario.AutomaticAccount, scenario.Owner));
            db.Contracts.AddRange(
                new RecurringContract { Id = scenario.ManualContract, FullWorthSpaceId = scenario.SpaceA, Name = "Manual", AccountId = scenario.ManualAccount, Amount = 100m, Currency = "EUR", BillingCycle = "monthly" },
                new RecurringContract { Id = scenario.UnderThresholdContract, FullWorthSpaceId = scenario.SpaceA, Name = "Under", AccountId = scenario.UnderThresholdAccount, Amount = 100m, Currency = "EUR", BillingCycle = "monthly" },
                new RecurringContract { Id = scenario.AutomaticContract, FullWorthSpaceId = scenario.SpaceA, Name = "Automatic", AccountId = scenario.AutomaticAccount, Amount = 50m, Currency = "EUR", BillingCycle = "monthly", AutoDetected = true });
            db.Transactions.AddRange(
                Transaction(scenario.ManualAccount, "manual-old", new(2026, 6, 5), 100m),
                Transaction(scenario.ManualAccount, "manual-new", new(2026, 8, 5), 106m, scenario.ManualEvidence),
                Transaction(scenario.UnderThresholdAccount, "under-old", new(2026, 6, 5), 100m),
                Transaction(scenario.UnderThresholdAccount, "under-new", new(2026, 8, 5), 103m),
                Transaction(scenario.AutomaticAccount, "automatic-old", new(2026, 6, 5), 50m),
                Transaction(scenario.AutomaticAccount, "automatic-new", new(2026, 8, 5), 60m));
            await db.SaveChangesAsync();
        });
        return scenario;
    }

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId, string name) => new()
    {
        Id = id, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "test", IdentificationHash = $"i5-{id:N}",
        ProviderAccountId = $"i5-{id:N}", InstitutionName = "I5 Bank", DisplayName = name, Currency = "EUR"
    };

    private static AccountOwner Owner(Guid accountId, Guid userId) => new() { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner };

    private static FinanceTransaction Transaction(Guid accountId, string key, DateOnly date, decimal amount, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(), AccountId = accountId, ExternalKey = key, Amount = -amount, Currency = "EUR", BookingDate = date,
        Counterparty = "I5 Vendor", NormalizedCounterparty = "I5 Vendor", Description = "I5 price change", RawJson = "{}"
    };

    private sealed record Scenario(
        Guid Owner, Guid Member, Guid Outside, Guid SpaceA, Guid Connection,
        Guid ManualAccount, Guid UnderThresholdAccount, Guid AutomaticAccount,
        Guid ManualContract, Guid UnderThresholdContract, Guid AutomaticContract,
        Guid ManualEvidence);
}
