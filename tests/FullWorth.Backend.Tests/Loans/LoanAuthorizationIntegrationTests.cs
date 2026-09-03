using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Loans;

public sealed class LoanAuthorizationIntegrationTests
{
    [Fact]
    public async Task OwnerCanManageLoanWhileMembersCanReadAndArchiveIsRetained()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/loans?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner, Payload("Home loan", scenario.CategoryA, scenario.AccountA)));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var createdJson = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var loanId = createdJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(250_000m, createdJson.RootElement.GetProperty("originalPrincipal").GetDecimal());
        Assert.Equal(scenario.CategoryA, createdJson.RootElement.GetProperty("categoryId").GetGuid());
        Assert.Equal(scenario.AccountA, createdJson.RootElement.GetProperty("accountId").GetGuid());

        using var memberRead = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/loans/{loanId}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, memberRead.StatusCode);

        using var memberWrite = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/loans/{loanId}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member, Payload("Member edit", scenario.CategoryA, scenario.AccountA)));
        Assert.Equal(HttpStatusCode.Forbidden, memberWrite.StatusCode);

        using var memberArchive = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/loans/{loanId}?fullWorthSpaceId={scenario.SpaceA}", scenario.Member));
        using var outsiderRead = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/loans/{loanId}?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));
        Assert.Equal(HttpStatusCode.Forbidden, memberArchive.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderRead.StatusCode);

        using var update = await client.SendAsync(UserRequest(HttpMethod.Put,
            $"/api/loans/{loanId}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner, Payload("Updated home loan", scenario.CategoryA, scenario.AccountA)));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var archive = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/loans/{loanId}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);

        using var archivedRead = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/loans/{loanId}?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner));
        using var archivedJson = JsonDocument.Parse(await archivedRead.Content.ReadAsStringAsync());
        Assert.False(archivedJson.RootElement.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task NonMembersAndCrossSpaceReferencesReturnNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var foreignCategory = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/loans?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner, Payload("Foreign category", scenario.CategoryB, scenario.AccountA)));
        using var foreignAccount = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/loans?fullWorthSpaceId={scenario.SpaceA}", scenario.Owner, Payload("Foreign account", scenario.CategoryA, scenario.AccountB)));
        using var outsiderList = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/loans?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside));
        using var outsiderCreate = await client.SendAsync(UserRequest(HttpMethod.Post,
            $"/api/loans?fullWorthSpaceId={scenario.SpaceA}", scenario.Outside, Payload("Outside", scenario.CategoryA, scenario.AccountA)));

        Assert.Equal(HttpStatusCode.NotFound, foreignCategory.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignAccount.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderList.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsiderCreate.StatusCode);
    }

    private static object Payload(string name, Guid categoryId, Guid accountId) => new
    {
        name,
        originalPrincipal = 250_000m,
        currentBalance = 200_000m,
        paymentAmount = 1_250m,
        nominalInterestRate = 3.5m,
        startDate = "2025-01-01",
        endDate = (string?)null,
        fixedTermMonths = 120,
        fees = 500m,
        paymentFrequency = "monthly",
        currency = "EUR",
        categoryId,
        accountId,
        isActive = true
    };

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
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM".ToUpperInvariant(),
                    DisplayName = $"Loan {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = scenario.SpaceA, Name = "Loan Space A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = scenario.SpaceB, Name = "Loan Space B", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceA, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.SpaceB, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner });

            db.Categories.AddRange(
                new FinanceCategory { Id = scenario.CategoryA, FullWorthSpaceId = scenario.SpaceA, Key = "loan-a", Name = "Loan A" },
                new FinanceCategory { Id = scenario.CategoryB, FullWorthSpaceId = scenario.SpaceB, Key = "loan-b", Name = "Loan B" });
            db.BankConnections.AddRange(
                Connection(scenario.ConnectionA, scenario.SpaceA),
                Connection(scenario.ConnectionB, scenario.SpaceB));
            db.Accounts.AddRange(
                Account(scenario.AccountA, scenario.SpaceA, scenario.ConnectionA),
                Account(scenario.AccountB, scenario.SpaceB, scenario.ConnectionB));
            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static BankConnection Connection(Guid id, Guid spaceId) => new()
    {
        Id = id, FullWorthSpaceId = spaceId, Provider = "test", InstitutionName = "Loan Bank", Country = "DE",
        ProviderSessionId = $"loan-{id:N}", Status = "AUTHORIZED"
    };

    private static FinanceAccount Account(Guid id, Guid spaceId, Guid connectionId) => new()
    {
        Id = id, FullWorthSpaceId = spaceId, BankConnectionId = connectionId, Provider = "test",
        IdentificationHash = $"loan-{id:N}", ProviderAccountId = $"loan-{id:N}", InstitutionName = "Loan Bank",
        DisplayName = "Loan account", Currency = "EUR"
    };

    private sealed record Scenario(
        Guid Owner, Guid Member, Guid Outside,
        Guid SpaceA, Guid SpaceB,
        Guid CategoryA, Guid CategoryB,
        Guid ConnectionA, Guid ConnectionB,
        Guid AccountA, Guid AccountB);
}
