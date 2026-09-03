using System.Net;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Migrations;

public sealed class WaveBSchemaConstraintTests
{
    [Fact]
    public async Task NormalizedEmailIsUnique()
    {
        using var factory = await StartAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        db.Users.Add(new FullWorthUser { EmailNormalized = "USER@EXAMPLE.COM", DisplayName = "One" });
        await db.SaveChangesAsync();
        db.Users.Add(new FullWorthUser { EmailNormalized = "USER@EXAMPLE.COM", DisplayName = "Two" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task MembershipCompositeKeyAndRoleCheckAreDatabaseEnforced()
    {
        using var factory = await StartAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        var user = new FullWorthUser { EmailNormalized = "MEMBER@EXAMPLE.COM", DisplayName = "Member" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var userId = user.Id;

        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Member
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Owner
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var invalidUser = new FullWorthUser { EmailNormalized = "INVALIDROLE@EXAMPLE.COM", DisplayName = "Invalid" };
        db.Users.Add(invalidUser);
        await db.SaveChangesAsync();
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            UserId = invalidUser.Id,
            Role = "admin"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AccountOwnerCompositeKeyAndOwnershipCheckAreDatabaseEnforced()
    {
        using var factory = await StartAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        var user = new FullWorthUser { EmailNormalized = "OWNER@EXAMPLE.COM", DisplayName = "Owner" };
        var connection = new BankConnection
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            InstitutionName = "Test Bank",
            Country = "DE"
        };
        var account = new FinanceAccount
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            BankConnectionId = connection.Id,
            Provider = "test",
            IdentificationHash = "schema-owner-account",
            ProviderAccountId = "schema-owner-account",
            InstitutionName = "Test Bank",
            DisplayName = "Account",
            Currency = "EUR"
        };
        db.AddRange(user, connection, account);
        await db.SaveChangesAsync();
        var userId = user.Id;
        var accountId = account.Id;

        db.AccountOwners.Add(new AccountOwner
        {
            AccountId = accountId,
            UserId = userId,
            OwnershipType = AccountOwnershipTypes.Owner
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.AccountOwners.Add(new AccountOwner
        {
            AccountId = accountId,
            UserId = userId,
            OwnershipType = AccountOwnershipTypes.Viewer
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var viewer = new FullWorthUser { EmailNormalized = "BADOWNERROLE@EXAMPLE.COM", DisplayName = "Viewer" };
        db.Users.Add(viewer);
        await db.SaveChangesAsync();
        db.AccountOwners.Add(new AccountOwner
        {
            AccountId = accountId,
            UserId = viewer.Id,
            OwnershipType = "editor"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CategoryKeyMayRepeatAcrossSpacesButNotInsideOneSpace()
    {
        using var factory = await StartAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();

        var secondSpace = new FullWorthSpace { Name = "Second", BaseCurrency = "EUR" };
        db.FullWorthSpaces.Add(secondSpace);
        await db.SaveChangesAsync();

        db.Categories.AddRange(
            new FinanceCategory { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = "schema-repeat", Name = "Legacy" },
            new FinanceCategory { FullWorthSpaceId = secondSpace.Id, Key = "schema-repeat", Name = "Second" });
        await db.SaveChangesAsync();

        db.Categories.Add(new FinanceCategory
        {
            FullWorthSpaceId = secondSpace.Id,
            Key = "schema-repeat",
            Name = "Duplicate"
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task<BackendWebApplicationFactory> StartAsync()
    {
        var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return factory;
    }
}
