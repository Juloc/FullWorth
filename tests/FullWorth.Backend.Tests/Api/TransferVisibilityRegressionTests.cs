using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class TransferVisibilityRegressionTests
{
    [Fact]
    public async Task TransactionDetail_DoesNotExposeHiddenTransferCounterpart()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedRevokedCounterpartScenario(factory);

        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/transactions/{scenario.VisibleTransaction:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", scenario.User));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("transferCounterpart").ValueKind);
    }

    [Fact]
    public async Task Unlink_DoesNotMutateHiddenCounterpartAfterAccessWasRevoked()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedRevokedCounterpartScenario(factory);

        using var response = await client.SendAsync(UserRequest(HttpMethod.Delete,
            $"/api/transactions/{scenario.VisibleTransaction:D}/transfer-link?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", scenario.User));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertPairStillLinked(factory, scenario);
    }

    [Fact]
    public async Task ClassificationCannotDemotePairWhenCounterpartIsNoLongerWritable()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var scenario = await SeedRevokedCounterpartScenario(factory);

        using var request = UserRequest(HttpMethod.Patch,
            $"/api/transactions/{scenario.VisibleTransaction:D}/classification?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", scenario.User);
        request.Content = JsonContent.Create(new
        {
            categoryId = (Guid?)null,
            isIgnored = false,
            isTransfer = false,
            transferPurpose = (string?)null,
            userNote = (string?)null
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await AssertPairStillLinked(factory, scenario);
    }

    private static async Task<Scenario> SeedRevokedCounterpartScenario(BackendWebApplicationFactory factory)
    {
        var user = Guid.NewGuid();
        var visibleAccount = Guid.NewGuid();
        var hiddenAccount = Guid.NewGuid();
        var visibleTransaction = Guid.NewGuid();
        var hiddenTransaction = Guid.NewGuid();
        var group = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Transfer Owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.AddRange(
                Account(visibleAccount, "Visible"),
                Account(hiddenAccount, "Hidden"));
            // Simulates a transfer pair created while both accounts were writable, followed by removal
            // of the user's grant on the counterpart account.
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = visibleAccount,
                UserId = user,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.AddRange(
                Transaction(visibleTransaction, visibleAccount, -100m, group),
                Transaction(hiddenTransaction, hiddenAccount, 100m, group));
            await db.SaveChangesAsync();
        });

        return new(user, visibleTransaction, hiddenTransaction, group);
    }

    private static async Task AssertPairStillLinked(BackendWebApplicationFactory factory, Scenario scenario)
    {
        await factory.SeedAsync(async db =>
        {
            var visible = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == scenario.VisibleTransaction);
            var hidden = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == scenario.HiddenTransaction);
            Assert.True(visible.IsTransfer);
            Assert.True(hidden.IsTransfer);
            Assert.Equal(scenario.GroupId, visible.TransferGroupId);
            Assert.Equal(scenario.GroupId, hidden.TransferGroupId);
        });
    }

    private static FinanceAccount Account(Guid id, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
        Provider = "manual",
        IdentificationHash = $"transfer-{id:N}",
        ProviderAccountId = $"transfer-{id:N}",
        InstitutionName = "Manual",
        DisplayName = name,
        Currency = "EUR",
        IsActive = true
    };

    private static FinanceTransaction Transaction(Guid id, Guid accountId, decimal amount, Guid group) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = $"transfer-{id:N}",
        Status = "BOOK",
        BookingDate = new DateOnly(2026, 8, 25),
        ValueDate = new DateOnly(2026, 8, 25),
        Amount = amount,
        Currency = "EUR",
        Counterparty = "Transfer",
        IsTransfer = true,
        TransferGroupId = group,
        CategorizationSource = "manual",
        RawJson = "{}"
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Scenario(Guid User, Guid VisibleTransaction, Guid HiddenTransaction, Guid GroupId);
}
