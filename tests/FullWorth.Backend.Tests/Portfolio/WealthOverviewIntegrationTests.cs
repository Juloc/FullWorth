using System.Data;
using System.Net;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Preferences;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class WealthOverviewIntegrationTests
{
    [Fact]
    public async Task OverviewCountsEachCanonicalSourceOnceAndExcludesInvestmentLinkedAccount()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            $"/api/wealth/overview?fullWorthSpaceId={scenario.Space}&currency=EUR",
            scenario.Owner));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(1_000m, root.GetProperty("accounts").GetProperty("amount").GetDecimal());
        Assert.Equal(2_000m, root.GetProperty("manualAssets").GetProperty("amount").GetDecimal());
        Assert.Equal(500m, root.GetProperty("investments").GetProperty("amount").GetDecimal());
        Assert.Equal(300m, root.GetProperty("loans").GetProperty("amount").GetDecimal());
        Assert.Equal(100m, root.GetProperty("otherLiabilities").GetProperty("amount").GetDecimal());
        Assert.Equal(2_500m, root.GetProperty("totalAssets").GetDecimal());
        Assert.Equal(400m, root.GetProperty("totalLiabilities").GetDecimal());
        Assert.Equal(3_100m, root.GetProperty("netWorth").GetDecimal());
        Assert.True(root.GetProperty("isComplete").GetBoolean());

        // The investment portfolio is linked to the second account whose bank balance is 500 EUR.
        // If that linked account were counted as both bank cash and portfolio value, net worth would be 3,600 EUR.
        Assert.NotEqual(3_600m, root.GetProperty("netWorth").GetDecimal());
    }

    [Fact]
    public async Task OverviewExposesConfiguredEmergencyFundProgressForSelectedGroup()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        var groupId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.AccountGroups.Add(new AccountGroup
            {
                Id = groupId,
                FullWorthSpaceId = scenario.Space,
                Name = "Emergency cash",
                SortOrder = 0
            });
            var cash = await db.Accounts.SingleAsync(account => account.Id == scenario.CashAccount);
            cash.GroupId = groupId;
            db.UserPreferences.Add(new UserPreference
            {
                FinanceUserId = scenario.Owner,
                FullWorthSpaceId = scenario.Space,
                Key = "wealth.emergencyFund",
                ValueJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    enabled = true,
                    targetAmount = 2_000m,
                    accountId = (Guid?)null,
                    accountGroupId = groupId
                })
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(
            $"/api/wealth/overview?fullWorthSpaceId={scenario.Space}&currency=EUR",
            scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var fund = json.RootElement.GetProperty("emergencyFund");
        Assert.True(fund.GetProperty("enabled").GetBoolean());
        Assert.Equal(2_000m, fund.GetProperty("targetAmount").GetDecimal());
        Assert.Equal(1_000m, fund.GetProperty("currentAmount").GetDecimal());
        Assert.Equal("EUR", fund.GetProperty("currency").GetString());
        Assert.Equal(groupId, fund.GetProperty("accountGroupId").GetGuid());
        Assert.True(fund.GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task MissingFxIsExplicitAndNeverFallsBackToOneToOne()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory, includeUsdAsset: true);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            $"/api/wealth/overview?fullWorthSpaceId={scenario.Space}&currency=EUR",
            scenario.Owner));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.False(root.GetProperty("isComplete").GetBoolean());
        Assert.Contains("USD", root.GetProperty("missingCurrencies").EnumerateArray().Select(item => item.GetString()));

        // The unconvertible 100 USD asset is skipped from the partial converted total, not silently treated as 100 EUR.
        Assert.Equal(2_000m, root.GetProperty("manualAssets").GetProperty("amount").GetDecimal());
        Assert.Equal(3_100m, root.GetProperty("netWorth").GetDecimal());
        Assert.Contains(root.GetProperty("manualAssets").GetProperty("originalAmounts").EnumerateArray(), item =>
            item.GetProperty("currency").GetString() == "USD" && item.GetProperty("amount").GetDecimal() == 100m);
    }

    [Fact]
    public async Task BookingActivityKeepsArchivedFinanzguruHistoryVisibleWithoutMakingItWealth()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        var importedAccountId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = importedAccountId,
                FullWorthSpaceId = scenario.Space,
                Provider = "finanzguru-import",
                IdentificationHash = $"fg-{importedAccountId:N}",
                ProviderAccountId = $"finanzguru:{importedAccountId:N}",
                InstitutionName = "Finanzguru Import",
                DisplayName = "Altes Girokonto",
                Currency = "EUR",
                IsActive = false,
                IncludeInNetWorth = false
            });
            db.AccountOwners.Add(Owner(importedAccountId, scenario.Owner));
            db.Transactions.AddRange(
                new FullWorth.Backend.Modules.Transactions.FinanceTransaction
                {
                    AccountId = importedAccountId,
                    ExternalKey = "finanzguru:old-2020",
                    Status = "BOOK",
                    BookingDate = new DateOnly(2020, 5, 12),
                    ValueDate = new DateOnly(2020, 5, 12),
                    Amount = -25m,
                    Currency = "EUR",
                    RawJson = "{}"
                },
                new FullWorth.Backend.Modules.Transactions.FinanceTransaction
                {
                    AccountId = importedAccountId,
                    ExternalKey = "finanzguru:old-2022",
                    Status = "BOOK",
                    BookingDate = new DateOnly(2022, 2, 4),
                    ValueDate = new DateOnly(2022, 2, 4),
                    Amount = 100m,
                    Currency = "EUR",
                    RawJson = "{}"
                });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(
            $"/api/wealth/booking-activity?fullWorthSpaceId={scenario.Space}&from=2020-01-01&to=2022-12-31",
            scenario.Owner));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var points = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, points.Length);
        Assert.Equal("2020-05-01", points[0].GetProperty("month").GetString());
        Assert.Equal(1, points[0].GetProperty("count").GetInt32());
        Assert.Equal(1, points[0].GetProperty("importedCount").GetInt32());
        Assert.Equal("2022-02-01", points[1].GetProperty("month").GetString());
        Assert.Equal(1, points[1].GetProperty("importedCount").GetInt32());

        using var history = await client.SendAsync(UserRequest(
            $"/api/wealth/history?fullWorthSpaceId={scenario.Space}&from=2020-01-01&to=2022-12-31",
            scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Empty(historyJson.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task NonMemberCannotReadWealthOverviewOrHistory()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var overview = await client.SendAsync(UserRequest(
            $"/api/wealth/overview?fullWorthSpaceId={scenario.Space}", scenario.Outside));
        using var history = await client.SendAsync(UserRequest(
            $"/api/wealth/history?fullWorthSpaceId={scenario.Space}", scenario.Outside));
        using var activity = await client.SendAsync(UserRequest(
            $"/api/wealth/booking-activity?fullWorthSpaceId={scenario.Space}", scenario.Outside));

        Assert.Equal(HttpStatusCode.NotFound, overview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, history.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, activity.StatusCode);
    }

    [Fact]
    public async Task LegacyHistoryKeepsUnknownDecompositionInsteadOfInventingInvestmentOrLoanHistory()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        var legacyDate = new DateOnly(2026, 7, 1);
        await factory.SeedAsync(async db =>
        {
            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Date = legacyDate,
                Currency = "EUR",
                Accounts = 100m,
                Assets = 50m,
                Liabilities = 20m,
                NetWorth = 130m
            });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            $"/api/wealth/history?fullWorthSpaceId={scenario.Space}&from={legacyDate:yyyy-MM-dd}&to={legacyDate:yyyy-MM-dd}&currency=EUR",
            scenario.Owner));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var point = Assert.Single(json.RootElement.EnumerateArray().ToArray());
        Assert.Equal(100m, point.GetProperty("accounts").GetDecimal());
        Assert.Equal(JsonValueKind.Null, point.GetProperty("manualAssets").ValueKind);
        Assert.Equal(JsonValueKind.Null, point.GetProperty("investments").ValueKind);
        Assert.Equal(JsonValueKind.Null, point.GetProperty("loans").ValueKind);
        Assert.Equal(JsonValueKind.Null, point.GetProperty("otherLiabilities").ValueKind);
        Assert.Equal(130m, point.GetProperty("netWorth").GetDecimal());
        Assert.False(point.GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task CurrentSnapshotStoresExplicitV2ComponentsAndLegacyTotalsRemainCompatible()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<NetWorthSnapshotService>();
            await service.CaptureForUserAsync(scenario.Space, scenario.Owner, CancellationToken.None);
        }

        await factory.SeedAsync(async db =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Accounts", "Assets", "Liabilities", "NetWorth",
                       "ManualAssets", "Investments", "Loans", "OtherLiabilities", "ComponentCurrency", "IsComplete"
                FROM "NetWorthSnapshots"
                WHERE "FullWorthSpaceId"=@space AND "UserId"=@user AND "Date"=@date AND "Currency"='EUR';
                """;
            AddParameter(command, "@space", scenario.Space);
            AddParameter(command, "@user", scenario.Owner);
            AddParameter(command, "@date", today);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1_000m, reader.GetDecimal(0));
            Assert.Equal(2_500m, reader.GetDecimal(1));
            Assert.Equal(400m, reader.GetDecimal(2));
            Assert.Equal(3_100m, reader.GetDecimal(3));
            Assert.Equal(2_000m, reader.GetDecimal(4));
            Assert.Equal(500m, reader.GetDecimal(5));
            Assert.Equal(300m, reader.GetDecimal(6));
            Assert.Equal(100m, reader.GetDecimal(7));
            Assert.Equal("EUR", reader.GetString(8));
            Assert.True(reader.GetBoolean(9));
        });
    }

    [Fact]
    public async Task V2PersistenceDoesNotRewriteLegacyNativeCurrencyRows()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory, includeUsdAsset: true, includeUsdRate: true);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<NetWorthSnapshotService>();
            await service.CaptureForUserAsync(scenario.Space, scenario.Owner, CancellationToken.None);
        }

        await factory.SeedAsync(async db =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT "Accounts", "Assets", "Liabilities", "NetWorth",
                       "ManualAssets", "Investments", "Loans", "OtherLiabilities", "ComponentCurrency"
                FROM "NetWorthSnapshots"
                WHERE "FullWorthSpaceId"=@space AND "UserId"=@user AND "Date"=@date AND "Currency"='EUR';
                """;
            AddParameter(command, "@space", scenario.Space);
            AddParameter(command, "@user", scenario.Owner);
            AddParameter(command, "@date", today);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            // Legacy EUR columns remain the native EUR row: the extra 100 USD asset is not folded into them.
            Assert.Equal(1_000m, reader.GetDecimal(0));
            Assert.Equal(2_500m, reader.GetDecimal(1));
            Assert.Equal(400m, reader.GetDecimal(2));
            Assert.Equal(3_100m, reader.GetDecimal(3));

            // V2 components are explicitly stored in EUR. At 1 EUR = 2 USD, the 100 USD asset adds 50 EUR.
            Assert.Equal(2_050m, reader.GetDecimal(4));
            Assert.Equal(500m, reader.GetDecimal(5));
            Assert.Equal(300m, reader.GetDecimal(6));
            Assert.Equal(100m, reader.GetDecimal(7));
            Assert.Equal("EUR", reader.GetString(8));
        });
    }

    private static async Task<Scenario> SeedScenarioAsync(
        BackendWebApplicationFactory factory,
        bool includeUsdAsset = false,
        bool includeUsdRate = false)
    {
        var scenario = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                User(scenario.Owner, "Owner"),
                User(scenario.Outside, "Outside"));
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.Space,
                Name = "Wealth test",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Role = FullWorthSpaceRoles.Owner
            });

            db.Accounts.AddRange(
                ManualAccount(scenario.CashAccount, scenario.Space, "Cash account"),
                ManualAccount(scenario.InvestmentLinkedAccount, scenario.Space, "Broker cash"));
            db.AccountOwners.AddRange(
                Owner(scenario.CashAccount, scenario.Owner),
                Owner(scenario.InvestmentLinkedAccount, scenario.Owner));
            db.BalanceSnapshots.AddRange(
                new BalanceSnapshot
                {
                    AccountId = scenario.CashAccount,
                    Amount = 1_000m,
                    Currency = "EUR",
                    BalanceType = "closingAvailable",
                    CapturedAt = DateTimeOffset.UtcNow
                },
                new BalanceSnapshot
                {
                    AccountId = scenario.InvestmentLinkedAccount,
                    Amount = 500m,
                    Currency = "EUR",
                    BalanceType = "closingAvailable",
                    CapturedAt = DateTimeOffset.UtcNow
                });

            db.Assets.Add(new Asset
            {
                FullWorthSpaceId = scenario.Space,
                Name = "Manual property value",
                Kind = AssetKinds.RealEstate,
                CurrentValue = 2_000m,
                Currency = "EUR",
                ValuedAt = new DateOnly(2026, 8, 1),
                IncludeInNetWorth = true
            });
            if (includeUsdAsset)
            {
                db.Assets.Add(new Asset
                {
                    FullWorthSpaceId = scenario.Space,
                    Name = "USD collectible",
                    Kind = AssetKinds.Collectible,
                    CurrentValue = 100m,
                    Currency = "USD",
                    ValuedAt = new DateOnly(2026, 8, 1),
                    IncludeInNetWorth = true
                });
            }
            if (includeUsdRate)
            {
                db.FxRates.Add(new FullWorth.Backend.Modules.Fx.FxRate
                {
                    Date = DateOnly.FromDateTime(DateTime.UtcNow),
                    Currency = "USD",
                    Rate = 2m,
                    FetchedAt = DateTimeOffset.UtcNow
                });
            }

            db.Loans.Add(new Loan
            {
                FullWorthSpaceId = scenario.Space,
                Name = "Mortgage",
                OriginalPrincipal = 500m,
                CurrentBalance = 300m,
                PaymentAmount = 10m,
                NominalInterestRate = 2m,
                StartDate = new DateOnly(2025, 1, 1),
                PaymentFrequency = "monthly",
                Currency = "EUR",
                IsActive = true
            });
            db.Liabilities.Add(new Liability
            {
                FullWorthSpaceId = scenario.Space,
                Name = "Other debt",
                Kind = "other",
                CurrentBalance = 100m,
                Currency = "EUR",
                IncludeInNetWorth = true
            });

            await db.SaveChangesAsync();

            var portfolioId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "InvestmentPortfolios"
                    ("Id", "FullWorthSpaceId", "Name", "Currency", "AccountId", "BenchmarkSecurityId",
                     "IsArchived", "IsManual", "IncludeInNetWorth", "CreatedAt", "UpdatedAt")
                VALUES
                    ({portfolioId}, {scenario.Space}, {"Test portfolio"}, {"EUR"}, {scenario.InvestmentLinkedAccount}, NULL,
                     FALSE, TRUE, TRUE, {now}, {now});
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "InvestmentTrades"
                    ("Id", "FullWorthSpaceId", "PortfolioId", "SecurityId", "TradeType", "TradeDate",
                     "Quantity", "Price", "GrossAmount", "Amount", "Currency", "Fees", "Taxes", "WithholdingTax",
                     "ExternalKey", "Notes", "Source", "CreatedAt", "UpdatedAt")
                VALUES
                    ({Guid.NewGuid()}, {scenario.Space}, {portfolioId}, NULL, {"deposit"}, {today},
                     NULL, NULL, NULL, {500m}, {"EUR"}, {0m}, {0m}, {0m},
                     NULL, NULL, {"manual"}, {now}, {now});
                """);
        });

        return scenario;
    }

    private static FullWorthUser User(Guid id, string label) => new()
    {
        Id = id,
        EmailNormalized = $"WEALTH-{id:N}@EXAMPLE.COM",
        DisplayName = label,
        IsActive = true
    };

    private static FinanceAccount ManualAccount(Guid id, Guid spaceId, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = spaceId,
        BankConnectionId = null,
        Provider = "manual",
        IdentificationHash = $"wealth-{id:N}",
        ProviderAccountId = $"manual-{id:N}",
        InstitutionName = "Manual",
        DisplayName = name,
        Currency = "EUR",
        IsActive = true,
        IncludeInNetWorth = true
    };

    private static AccountOwner Owner(Guid accountId, Guid userId) => new()
    {
        AccountId = accountId,
        UserId = userId,
        OwnershipType = AccountOwnershipTypes.Owner
    };

    private static HttpRequestMessage UserRequest(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record Scenario(
        Guid Owner,
        Guid Outside,
        Guid Space,
        Guid CashAccount,
        Guid InvestmentLinkedAccount,
        Guid Unused);
}
