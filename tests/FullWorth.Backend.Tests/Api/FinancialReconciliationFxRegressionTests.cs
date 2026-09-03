using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Api;

public sealed class FinancialReconciliationFxRegressionTests
{
    [Fact]
    public async Task AnalyticsMarksMissingFxIncompleteAndNeverAssumesOneToOne()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "FX report user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"fx-{accountId:N}",
                ProviderAccountId = $"fx-{accountId:N}",
                InstitutionName = "Test",
                DisplayName = "USD account",
                Currency = "USD",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = accountId,
                ExternalKey = $"fx-missing:{Guid.NewGuid():N}",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 8, 15),
                ValueDate = new DateOnly(2026, 8, 15),
                Amount = -100m,
                Currency = "USD",
                Counterparty = "No FX merchant",
                NormalizedCounterparty = "NO FX MERCHANT",
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        });

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/analytics/query?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        request.Content = JsonContent.Create(new
        {
            measure = "spend",
            dimension = "month",
            from = "2026-08-01",
            to = "2026-08-31",
            granularity = "month",
            accountIds = new[] { accountId },
            accountGroupIds = Array.Empty<Guid>(),
            categoryScopes = Array.Empty<object>(),
            tagIds = Array.Empty<Guid>(),
            normalizedMerchants = Array.Empty<string>(),
            contractIds = Array.Empty<Guid>(),
            currencies = Array.Empty<string>(),
            directions = new[] { "expense" },
            includeTransfers = false,
            includePending = false,
            includeIgnored = false,
            refundMode = "reverse"
        });

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(0m, json.RootElement.GetProperty("total").GetDecimal());
        Assert.DoesNotContain("100", json.RootElement.GetProperty("series").ToString(), StringComparison.Ordinal);
    }
}
