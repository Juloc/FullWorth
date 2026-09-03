using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Api;

public sealed class BackendApiBaselineTests
{
    [Fact]
    public async Task HealthEndpointReturnsBackendStatus()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("fullworth-backend", json.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task TransactionReadApiDoesNotExposeRawJson()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorth.Backend.Modules.Users.FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "API user",
                IsActive = true
            });
            var (connection, account) = CreateAccount("raw-json-account");
            db.BankConnections.Add(connection);
            db.Accounts.Add(account);
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account.Id,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId,
                AccountId = account.Id,
                ExternalKey = "raw-json-transaction",
                Amount = -7.89m,
                Currency = "EUR",
                RawJson = "{\"secretMarker\":\"must-not-leak\"}"
            });
            await db.SaveChangesAsync();
        });

        using var request = CreateUserRequest(
            $"/api/transactions/{transactionId}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.False(body.Contains("RawJson", StringComparison.OrdinalIgnoreCase));
        Assert.False(body.Contains("secretMarker", StringComparison.OrdinalIgnoreCase));
        Assert.False(body.Contains("must-not-leak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PurchaseReconciliationReportsTotalsLinkedAmountAndRemainders()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorth.Backend.Modules.Users.FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "API user",
                IsActive = true
            });
            var (connection, account) = CreateAccount("purchase-account");
            var transaction = new FinanceTransaction
            {
                AccountId = account.Id,
                ExternalKey = "purchase-transaction",
                Amount = -30.00m,
                Currency = "EUR"
            };
            var purchase = new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                TransactionId = transaction.Id,
                Source = "receipt",
                Merchant = "Test Store",
                PurchaseDate = new DateOnly(2026, 8, 10),
                TotalAmount = 25.00m,
                Currency = "EUR",
                Items =
                [
                    new PurchaseItem { Name = "Item A", TotalPrice = 12.00m, Currency = "EUR" },
                    new PurchaseItem { Name = "Item B", TotalPrice = 8.00m, Currency = "EUR" }
                ]
            };

            db.BankConnections.Add(connection);
            db.Accounts.Add(account);
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account.Id,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.Add(transaction);
            db.Purchases.Add(purchase);
            await db.SaveChangesAsync();
        });

        using var request = CreateUserRequest(
            $"/api/purchases/{purchaseId}/reconciliation?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = json.RootElement;
        Assert.Equal(25.00m, result.GetProperty("purchaseTotal").GetDecimal());
        Assert.Equal(20.00m, result.GetProperty("itemTotal").GetDecimal());
        Assert.Equal(5.00m, result.GetProperty("itemDifference").GetDecimal());
        Assert.Equal(-30.00m, result.GetProperty("transactionAmount").GetDecimal());
        Assert.Equal(5.00m, result.GetProperty("transactionDifference").GetDecimal());
        Assert.False(result.GetProperty("itemsReconciled").GetBoolean());
        Assert.False(result.GetProperty("transactionReconciled").GetBoolean());
        Assert.False(result.GetProperty("fullyReconciled").GetBoolean());
    }

    private static HttpRequestMessage CreateUserRequest(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static (BankConnection Connection, FinanceAccount Account) CreateAccount(string identificationHash)
    {
        var connection = new BankConnection
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            Provider = "test",
            InstitutionName = "Test Bank",
            Country = "DE",
            ProviderSessionId = $"session-{identificationHash}"
        };
        var account = new FinanceAccount
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            BankConnectionId = connection.Id,
            Provider = "test",
            IdentificationHash = identificationHash,
            ProviderAccountId = identificationHash,
            InstitutionName = "Test Bank",
            DisplayName = "Test account",
            Currency = "EUR"
        };
        return (connection, account);
    }
}
