using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Api;

public sealed class LegacyParityCapabilityRegressionTests
{
    [Fact]
    public async Task ViewerWithAccountOwnershipCannotBypassCapabilitiesThroughLegacyOrIntegratedRoutes()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var user = Guid.NewGuid();
        var account = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Viewer owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = FullWorthSpaceRoles.Member
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{account:N}",
                IdentificationHash = $"manual-{account:N}",
                InstitutionName = "Manual",
                DisplayName = "Owned account",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = user,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });

        using var appearance = UserRequest(HttpMethod.Put,
            $"/api/account-experience/{account:D}/appearance?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        appearance.Content = JsonContent.Create(new { icon = "wallet", iconColor = "#112233", backgroundColor = "#FFFFFF" });
        using var appearanceResponse = await client.SendAsync(appearance);
        Assert.Equal(HttpStatusCode.Forbidden, appearanceResponse.StatusCode);

        using var reorder = UserRequest(HttpMethod.Post,
            $"/api/account-experience/reorder?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        reorder.Content = JsonContent.Create(new
        {
            groups = Array.Empty<object>(),
            accounts = new[] { new { accountId = account, groupId = (Guid?)null, sortOrder = 0 } }
        });
        using var reorderResponse = await client.SendAsync(reorder);
        Assert.Equal(HttpStatusCode.Forbidden, reorderResponse.StatusCode);

        using var upload = UserRequest(HttpMethod.Post,
            $"/api/import-jobs/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        upload.Content = new MultipartFormDataContent();
        using var uploadResponse = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.Forbidden, uploadResponse.StatusCode);

        // Main-integrated receipt/Amazon writes are FullWorth-Space member endpoints on their own. The
        // parity capability bridge must stop viewers before request parsing or external work begins.
        using var receipt = UserRequest(HttpMethod.Post,
            $"/api/purchases/receipt-scan/jobs?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        receipt.Content = new MultipartFormDataContent();
        using var receiptResponse = await client.SendAsync(receipt);
        Assert.Equal(HttpStatusCode.Forbidden, receiptResponse.StatusCode);

        using var amazonSync = UserRequest(HttpMethod.Post,
            $"/api/purchases/amazon/sync?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        amazonSync.Content = JsonContent.Create(new { historyDays = 90 });
        using var amazonResponse = await client.SendAsync(amazonSync);
        Assert.Equal(HttpStatusCode.Forbidden, amazonResponse.StatusCode);

        // Personal read-state is intentionally not a banking mutation capability.
        using var seen = UserRequest(HttpMethod.Post,
            $"/api/account-experience/{account:D}/seen?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var seenResponse = await client.SendAsync(seen);
        Assert.Equal(HttpStatusCode.NoContent, seenResponse.StatusCode);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
