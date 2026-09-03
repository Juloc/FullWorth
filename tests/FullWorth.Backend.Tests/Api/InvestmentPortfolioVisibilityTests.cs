using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentPortfolioVisibilityTests
{
    [Fact]
    public async Task LegacyPortfolioListDoesNotExposePortfoliosLinkedToHiddenAccounts()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var visibleAccountId = Guid.NewGuid();
        var hiddenAccountId = Guid.NewGuid();
        var visiblePortfolioId = Guid.NewGuid();
        var hiddenPortfolioId = Guid.NewGuid();
        var unlinkedPortfolioId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Investment viewer",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = "member"
            });

            db.Accounts.AddRange(
                Account(visibleAccountId, "Visible investment account"),
                Account(hiddenAccountId, "Hidden investment account"));
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = visibleAccountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Viewer
            });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES
({visiblePortfolioId},{FullWorthSpaceDefaults.LegacyId},{"Visible Depot"},{"EUR"},{visibleAccountId},{true},{true},{false},{now},{now}),
({hiddenPortfolioId},{FullWorthSpaceDefaults.LegacyId},{"Hidden Depot"},{"EUR"},{hiddenAccountId},{true},{true},{false},{now},{now}),
({unlinkedPortfolioId},{FullWorthSpaceDefaults.LegacyId},{"Manual Depot"},{"EUR"},{null},{true},{true},{false},{now},{now})
""");
        });

        using var request = UserRequest(HttpMethod.Get,
            $"/api/investments/portfolios?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = document.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("id").GetGuid())
            .ToHashSet();

        Assert.Contains(visiblePortfolioId, ids);
        Assert.Contains(unlinkedPortfolioId, ids);
        Assert.DoesNotContain(hiddenPortfolioId, ids);
    }

    private static FinanceAccount Account(Guid id, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Provider = "manual",
        IdentificationHash = $"investment-visibility-{id:N}",
        ProviderAccountId = $"investment-visibility-{id:N}",
        InstitutionName = "Manual",
        DisplayName = name,
        Currency = "EUR",
        IsActive = true
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
