using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts;

/// <summary>
/// Security regression (Wave N5): POST /api/contracts/detection/accept creates/updates a contract, so
/// its BOLA/IDOR guards must hold — a non-member sees 404, a member-non-owner 403, and a supplied
/// cross-space category or non-owned account is rejected (404/403) rather than mutating foreign data.
/// </summary>
public sealed class ContractDetectionAcceptAuthorizationTests
{
    [Fact]
    public async Task Accept_EnforcesSpaceRoleAndAccountCategoryOwnership()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var path = $"/api/contracts/detection/accept?fullWorthSpaceId={s.SpaceA}";

        // Non-member of the space -> 404 (no existence leak).
        using var outsider = await client.SendAsync(Request(path, s.Outsider, Candidate()));
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);

        // Member but not owner -> 403.
        using var member = await client.SendAsync(Request(path, s.Member, Candidate()));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);

        // Owner supplying a category from another space -> 404.
        using var crossCategory = await client.SendAsync(Request(path, s.Owner, Candidate(categoryId: s.CategoryB)));
        Assert.Equal(HttpStatusCode.NotFound, crossCategory.StatusCode);

        // Space owner who is only a *viewer* of the supplied account -> 403.
        using var viewer = await client.SendAsync(Request(path, s.OwnerViewer, Candidate(accountId: s.Account)));
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);

        // Owner with an in-space category and an owned account -> success.
        using var ok = await client.SendAsync(Request(path, s.Owner, Candidate(categoryId: s.CategoryA, accountId: s.Account)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    private static object Candidate(Guid? categoryId = null, Guid? accountId = null) => new
    {
        counterparty = "NETFLIX",
        typicalAmount = 12.99m,
        currency = "EUR",
        billingCycle = "monthly",
        interval = 1,
        lastPaymentDate = "2026-07-01",
        nextDueDate = "2026-08-01",
        categoryId,
        accountId,
        samples = 6,
        amountVariation = 0m,
        confidence = 0.9m
    };

    private static HttpRequestMessage Request(string path, Guid userId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            foreach (var id in new[] { s.Owner, s.OwnerViewer, s.Member, s.Outsider })
                db.Users.Add(new FullWorthUser { Id = id, EmailNormalized = $"{id:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = $"N5 {id:N}", IsActive = true });

            db.FullWorthSpaces.AddRange(
                new FullWorthSpace { Id = s.SpaceA, Name = "N5 A", BaseCurrency = "EUR" },
                new FullWorthSpace { Id = s.SpaceB, Name = "N5 B", BaseCurrency = "EUR" });

            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceA, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceA, UserId = s.OwnerViewer, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceA, UserId = s.Member, Role = FullWorthSpaceRoles.Member });

            db.BankConnections.Add(new BankConnection
            {
                Id = s.Connection,
                FullWorthSpaceId = s.SpaceA,
                Provider = "test",
                InstitutionName = "N5 Bank",
                Country = "DE",
                ProviderSessionId = $"n5-{s.Connection:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = s.Account,
                FullWorthSpaceId = s.SpaceA,
                BankConnectionId = s.Connection,
                Provider = "test",
                IdentificationHash = $"n5-{s.Account:N}",
                ProviderAccountId = $"provider-{s.Account:N}",
                InstitutionName = "N5 Bank",
                DisplayName = "N5 Account",
                Currency = "EUR"
            });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = s.Account, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = s.Account, UserId = s.OwnerViewer, OwnershipType = AccountOwnershipTypes.Viewer });

            db.Categories.AddRange(
                new FinanceCategory { Id = s.CategoryA, FullWorthSpaceId = s.SpaceA, Key = $"n5-a-{s.CategoryA:N}", Name = "N5 A" },
                new FinanceCategory { Id = s.CategoryB, FullWorthSpaceId = s.SpaceB, Key = $"n5-b-{s.CategoryB:N}", Name = "N5 B" });

            await db.SaveChangesAsync();
        });

        return s;
    }

    private sealed record Scenario(
        Guid Owner, Guid OwnerViewer, Guid Member, Guid Outsider,
        Guid SpaceA, Guid SpaceB, Guid Connection,
        Guid Account, Guid CategoryA)
    {
        public Guid CategoryB { get; } = Guid.NewGuid();
    }
}
