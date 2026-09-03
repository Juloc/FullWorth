using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts.Review;

public sealed class ContractCandidateReviewTests
{
    [Fact]
    public async Task DismissingACandidateRemovesItFromDetection()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        Assert.True(await DetectionContainsAsync(client, scenario, scenario.Owner, "NETFLIX"));

        using var dismiss = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/detection/dismiss?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { counterparty = "NETFLIX", currency = "EUR" }));
        Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);

        Assert.False(await DetectionContainsAsync(client, scenario, scenario.Owner, "NETFLIX"));
    }

    [Fact]
    public async Task DismissIsIdempotent()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var dismiss = await client.SendAsync(UserRequest(HttpMethod.Post,
                $"/api/contracts/detection/dismiss?fullWorthSpaceId={scenario.Space}", scenario.Owner,
                new { counterparty = "NETFLIX", currency = "EUR" }));
            Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);
        }
    }

    [Fact]
    public async Task MemberCannotDismiss_AndOutsiderGets404()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var member = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/detection/dismiss?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new { counterparty = "NETFLIX", currency = "EUR" }));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        using var outsider = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/detection/dismiss?fullWorthSpaceId={scenario.Space}", scenario.Outside,
            new { counterparty = "NETFLIX", currency = "EUR" }));
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
    }

    [Fact]
    public async Task Dismiss_RejectsInvalidCurrencyWithProblemDetails()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/contracts/detection/dismiss?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { counterparty = "NETFLIX", currency = "EURO" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<bool> DetectionContainsAsync(HttpClient client, Scenario scenario, Guid userId, string counterparty)
    {
        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/contracts/detection?fullWorthSpaceId={scenario.Space}", userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray()
            .Any(candidate => candidate.GetProperty("counterparty").GetString() == counterparty);
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
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"I6 {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "I6 Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member });

            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.Connection,
                FullWorthSpaceId = scenario.Space,
                Provider = "test",
                InstitutionName = "I6 Bank",
                Country = "DE",
                ProviderSessionId = $"i6-{scenario.Connection:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.Account,
                FullWorthSpaceId = scenario.Space,
                BankConnectionId = scenario.Connection,
                Provider = "test",
                IdentificationHash = $"i6-{scenario.Account:N}",
                ProviderAccountId = $"provider-{scenario.Account:N}",
                InstitutionName = "I6 Bank",
                DisplayName = "I6 Account",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = scenario.Account, UserId = scenario.Owner, OwnershipType = AccountOwnershipTypes.Owner });

            // Six regular monthly payments -> a high-confidence recurring-contract candidate.
            foreach (var month in new[] { 2, 3, 4, 5, 6, 7 })
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = scenario.Account,
                    ExternalKey = $"NETFLIX-{month}",
                    Amount = -12.99m,
                    Currency = "EUR",
                    BookingDate = new DateOnly(2026, month, 2),
                    Counterparty = "Netflix",
                    NormalizedCounterparty = "NETFLIX",
                    RawJson = "{}"
                });
            }

            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private sealed record Scenario(
        Guid Owner,
        Guid Member,
        Guid Outside,
        Guid Space,
        Guid Connection,
        Guid Account);
}
