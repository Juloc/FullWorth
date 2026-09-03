using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentLegacyCapabilityRegressionTests
{
    [Fact]
    public async Task EditorCapabilityCanUpdateLegacyPortfolioWithoutSpaceOwnerRole()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory, denyInvestments: false);

        using var request = UserRequest(HttpMethod.Put,
            $"/api/investments/portfolios/{scenario.PortfolioId:D}?fullWorthSpaceId={scenario.SpaceId:D}",
            scenario.UserId);
        request.Content = JsonContent.Create(new
        {
            name = "Editor renamed depot",
            currency = "EUR",
            accountId = scenario.AccountId,
            benchmarkSecurityId = (Guid?)null,
            isArchived = false
        });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Name\" FROM \"InvestmentPortfolios\" WHERE \"Id\"=@id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = scenario.PortfolioId;
            command.Parameters.Add(parameter);
            Assert.Equal("Editor renamed depot", Convert.ToString(await command.ExecuteScalarAsync()));
        });
    }

    [Fact]
    public async Task ExplicitCapabilityDenialBlocksLegacyPortfolioMutation()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedAsync(factory, denyInvestments: true);

        using var request = UserRequest(HttpMethod.Put,
            $"/api/investments/portfolios/{scenario.PortfolioId:D}?fullWorthSpaceId={scenario.SpaceId:D}",
            scenario.UserId);
        request.Content = JsonContent.Create(new
        {
            name = "Must not change",
            currency = "EUR",
            accountId = scenario.AccountId,
            benchmarkSecurityId = (Guid?)null,
            isArchived = false
        });

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, bool denyInvestments)
    {
        var userId = Guid.NewGuid();
        var spaceId = FullWorthSpaceDefaults.LegacyId;
        var accountId = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Investment editor",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = "member"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = $"investment-capability-{accountId:N}",
                ProviderAccountId = $"investment-capability-{accountId:N}",
                InstitutionName = "Manual",
                DisplayName = "Editor account",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceMemberRoleTemplates" ("FullWorthSpaceId","UserId","Template","UpdatedAt")
VALUES ({spaceId},{userId},{"editor"},{now});

INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{spaceId},{"Editor depot"},{"EUR"},{accountId},{true},{true},{false},{now},{now});
""");

            if (denyInvestments)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({spaceId},{userId},{"investments.manage"},{false},{now});
""");
            }
        });

        return new Scenario(userId, spaceId, accountId, portfolioId);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Scenario(Guid UserId, Guid SpaceId, Guid AccountId, Guid PortfolioId);
}
