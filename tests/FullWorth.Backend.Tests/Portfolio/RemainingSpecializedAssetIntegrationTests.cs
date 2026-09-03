using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class RemainingSpecializedAssetIntegrationTests
{
    [Fact]
    public async Task DescriptiveAndReferenceValuesNeverOverwriteAcceptedAssetValue()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var collectible = NewAsset(space, AssetKinds.Collectible, "Watch", 1_000m);
        var business = NewAsset(space, AssetKinds.BusinessInterest, "Company", 20_000m);
        var pension = NewAsset(space, AssetKinds.InsurancePension, "Pension", 15_000m);
        await SeedAsync(factory, owner, null, null, space, [collectible, business, pension]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{collectible.Id}/collectible?fullWorthSpaceId={space}", owner, new
            {
                category = "watch", maker = "Maker", model = "Model", serialNumber = "SERIAL-SECRET",
                purchasePrice = 900m, purchaseCurrency = "EUR", insuredValue = 3_000m,
                appraisedValue = 2_500m, appraisedAt = "2026-08-01"
            }))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{business.Id}/business-interest?fullWorthSpaceId={space}", owner, new
            {
                companyDisplayName = "Private GmbH", legalForm = "GmbH", ownershipPercent = 25m,
                investedCapital = 50_000m, investedCurrency = "EUR", valuationMethod = "book_value"
            }))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put,
            $"/api/assets/{pension.Id}/insurance-pension?fullWorthSpaceId={space}", owner, new
            {
                providerName = "Provider", productName = "Plan", productType = "pension", policyReference = "POLICY-SECRET",
                regularContribution = 300m, contributionCycle = "monthly", guaranteedValue = 80_000m,
                guaranteedValueDate = "2045-01-01"
            }))).StatusCode);

        Assert.Equal(1_000m, await CurrentValueAsync(factory, collectible.Id));
        Assert.Equal(20_000m, await CurrentValueAsync(factory, business.Id));
        Assert.Equal(15_000m, await CurrentValueAsync(factory, pension.Id));
    }

    [Fact]
    public async Task ReceivablePaymentReducesPrincipalAndAcceptedValueButInterestDoesNot()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var receivable = NewAsset(space, AssetKinds.Receivable, "Private loan", 8_000m);
        await SeedAsync(factory, owner, null, null, space, [receivable]);
        using var client = factory.CreateClient();

        await PutReceivableAsync(client, owner, space, receivable.Id, 10_000m, 10_000m);

        var payment = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{receivable.Id}/receivable/payments?fullWorthSpaceId={space}", owner, new
            {
                date = "2026-09-02", principalAmount = 2_000m, interestAmount = 100m, currency = "EUR"
            }));
        Assert.Equal(HttpStatusCode.OK, payment.StatusCode);
        using (var json = JsonDocument.Parse(await payment.Content.ReadAsStringAsync()))
        {
            Assert.Equal(8_000m, json.RootElement.GetProperty("detail").GetProperty("outstandingPrincipal").GetDecimal());
            Assert.Equal(6_000m, json.RootElement.GetProperty("acceptedAssetValue").GetDecimal());
        }

        var interestOnly = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{receivable.Id}/receivable/payments?fullWorthSpaceId={space}", owner, new
            {
                date = "2026-09-03", principalAmount = 0m, interestAmount = 50m, currency = "EUR"
            }));
        Assert.Equal(HttpStatusCode.OK, interestOnly.StatusCode);
        using (var json = JsonDocument.Parse(await interestOnly.Content.ReadAsStringAsync()))
        {
            Assert.Equal(8_000m, json.RootElement.GetProperty("detail").GetProperty("outstandingPrincipal").GetDecimal());
            Assert.Equal(6_000m, json.RootElement.GetProperty("acceptedAssetValue").GetDecimal());
        }

        var history = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{receivable.Id}/valuations?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Contains(historyJson.RootElement.EnumerateArray(), valuation =>
            valuation.GetProperty("isCurrent").GetBoolean() && valuation.GetProperty("amount").GetDecimal() == 6_000m);
    }

    [Fact]
    public async Task ReceivableWriteDownChangesAcceptedValueButPreservesLegalOutstandingPrincipal()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var receivable = NewAsset(space, AssetKinds.Receivable, "Private loan", 8_000m);
        await SeedAsync(factory, owner, null, null, space, [receivable]);
        using var client = factory.CreateClient();
        await PutReceivableAsync(client, owner, space, receivable.Id, 10_000m, 8_000m);

        var unconfirmed = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{receivable.Id}/receivable/write-down?fullWorthSpaceId={space}", owner,
            new { recoverableAmount = 2_500m, confirmed = false }));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);

        var writtenDown = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{receivable.Id}/receivable/write-down?fullWorthSpaceId={space}", owner,
            new { recoverableAmount = 2_500m, confirmed = true }));
        Assert.Equal(HttpStatusCode.OK, writtenDown.StatusCode);
        using var json = JsonDocument.Parse(await writtenDown.Content.ReadAsStringAsync());
        Assert.Equal("written_off", json.RootElement.GetProperty("detail").GetProperty("status").GetString());
        Assert.Equal(8_000m, json.RootElement.GetProperty("detail").GetProperty("outstandingPrincipal").GetDecimal());
        Assert.Equal(2_500m, json.RootElement.GetProperty("acceptedAssetValue").GetDecimal());
        Assert.Equal(2_500m, await CurrentValueAsync(factory, receivable.Id));
    }

    [Fact]
    public async Task LinkedReceivablePaymentRequiresAccessibleSameSpaceIncomeTransaction()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var otherSpace = Guid.NewGuid();
        var receivable = NewAsset(space, AssetKinds.Receivable, "Private loan", 10_000m);
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var otherTransactionId = Guid.NewGuid();

        await SeedAsync(factory, owner, null, otherOwner, space, [receivable]);
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = otherSpace, Name = "Other", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = otherSpace, UserId = otherOwner, Role = FullWorthSpaceRoles.Owner });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId, FullWorthSpaceId = space, Provider = "manual", IdentificationHash = Guid.NewGuid().ToString("N"),
                ProviderAccountId = "receivable-income", InstitutionName = "Manual", DisplayName = "Income", Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Accounts.Add(new FinanceAccount
            {
                Id = otherAccountId, FullWorthSpaceId = otherSpace, Provider = "manual", IdentificationHash = Guid.NewGuid().ToString("N"),
                ProviderAccountId = "other-income", InstitutionName = "Manual", DisplayName = "Other", Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = otherAccountId, UserId = otherOwner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId, AccountId = accountId, ExternalKey = "receivable-payment", Amount = 2_100m, Currency = "EUR", BookingDate = new DateOnly(2026, 9, 2)
            });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = otherTransactionId, AccountId = otherAccountId, ExternalKey = "other-payment", Amount = 2_100m, Currency = "EUR", BookingDate = new DateOnly(2026, 9, 2)
            });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();
        await PutReceivableAsync(client, owner, space, receivable.Id, 10_000m, 10_000m);

        var inaccessible = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{receivable.Id}/receivable/payments?fullWorthSpaceId={space}", owner, new
            {
                transactionId = otherTransactionId, date = "2026-09-02", principalAmount = 2_000m, interestAmount = 100m, currency = "EUR"
            }));
        Assert.Equal(HttpStatusCode.NotFound, inaccessible.StatusCode);

        var linked = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{receivable.Id}/receivable/payments?fullWorthSpaceId={space}", owner, new
            {
                transactionId, date = "2026-09-02", principalAmount = 2_000m, interestAmount = 100m, currency = "EUR"
            }));
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);

        var duplicate = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{receivable.Id}/receivable/payments?fullWorthSpaceId={space}", owner, new
            {
                transactionId, date = "2026-09-02", principalAmount = 100m, interestAmount = 0m, currency = "EUR"
            }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task ReceivablePaymentMasksHiddenTransactionAndCannotOverAllocateItAcrossAssets()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var space = Guid.NewGuid();
        var first = NewAsset(space, AssetKinds.Receivable, "Loan A", 5_000m);
        var second = NewAsset(space, AssetKinds.Receivable, "Loan B", 5_000m);
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        await SeedAsync(factory, owner, member, null, space, [first, second]);
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId, FullWorthSpaceId = space, Provider = "manual", IdentificationHash = Guid.NewGuid().ToString("N"),
                ProviderAccountId = "private-income", InstitutionName = "Manual", DisplayName = "Private account", Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId, AccountId = accountId, ExternalKey = "shared-payment", Amount = 1_000m, Currency = "EUR",
                BookingDate = new DateOnly(2026, 9, 2), Counterparty = "Sensitive counterparty"
            });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateClient();
        await PutReceivableAsync(client, owner, space, first.Id, 5_000m, 5_000m);
        await PutReceivableAsync(client, owner, space, second.Id, 5_000m, 5_000m);

        var firstPayment = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{first.Id}/receivable/payments?fullWorthSpaceId={space}", owner,
            new { transactionId, date = "2026-09-02", principalAmount = 700m, interestAmount = 100m, currency = "EUR" }));
        Assert.Equal(HttpStatusCode.OK, firstPayment.StatusCode);

        var overAllocation = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/assets/{second.Id}/receivable/payments?fullWorthSpaceId={space}", owner,
            new { transactionId, date = "2026-09-02", principalAmount = 250m, interestAmount = 0m, currency = "EUR" }));
        Assert.Equal(HttpStatusCode.BadRequest, overAllocation.StatusCode);

        var memberList = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/assets/{first.Id}/receivable/payments?fullWorthSpaceId={space}", member));
        Assert.Equal(HttpStatusCode.OK, memberList.StatusCode);
        using var json = JsonDocument.Parse(await memberList.Content.ReadAsStringAsync());
        var row = json.RootElement.EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, row.GetProperty("transactionId").ValueKind);
    }

    [Fact]
    public async Task EveryRemainingSubtypeIsOwnerWritableMemberReadableAndHiddenFromOutsideUser()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var outside = Guid.NewGuid();
        var space = Guid.NewGuid();
        var assets = new[]
        {
            NewAsset(space, AssetKinds.Collectible, "Collectible", 1m),
            NewAsset(space, AssetKinds.Receivable, "Receivable", 1m),
            NewAsset(space, AssetKinds.BusinessInterest, "Business", 1m),
            NewAsset(space, AssetKinds.InsurancePension, "Pension", 1m)
        };
        await SeedAsync(factory, owner, member, outside, space, assets);
        using var client = factory.CreateClient();

        var cases = new (Asset Asset, string Path, object Body)[]
        {
            (assets[0], "collectible", new { category = "art" }),
            (assets[1], "receivable", new { counterpartyDisplayLabel = "Person", originalPrincipal = 1m, outstandingPrincipal = 1m, currency = "EUR", status = "active" }),
            (assets[2], "business-interest", new { companyDisplayName = "Company", ownershipPercent = 10m, valuationMethod = "manual" }),
            (assets[3], "insurance-pension", new { productType = "pension", providerName = "Provider" })
        };

        foreach (var item in cases)
        {
            var path = $"/api/assets/{item.Asset.Id}/{item.Path}?fullWorthSpaceId={space}";
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, path, owner, item.Body))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Get, path, member))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Request(HttpMethod.Put, path, member, item.Body))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request(HttpMethod.Get, path, outside))).StatusCode);
        }
    }

    private static async Task PutReceivableAsync(HttpClient client, Guid owner, Guid space, Guid assetId, decimal original, decimal outstanding)
    {
        var response = await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{assetId}/receivable?fullWorthSpaceId={space}", owner, new
        {
            counterpartyDisplayLabel = "Person", originalPrincipal = original, outstandingPrincipal = outstanding,
            currency = "EUR", interestRate = 3m, startDate = "2025-01-01", dueDate = "2030-01-01", paymentCycle = "monthly",
            expectedPayment = 300m, status = outstanding == 0m ? "settled" : "active"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Asset NewAsset(Guid space, string kind, string name, decimal value) => new()
    {
        Id = Guid.NewGuid(), FullWorthSpaceId = space, Name = name, Kind = kind, CurrentValue = value, Currency = "EUR", IncludeInNetWorth = true
    };

    private static async Task<decimal> CurrentValueAsync(BackendWebApplicationFactory factory, Guid assetId)
    {
        decimal value = -1m;
        await factory.SeedAsync(async db => { value = (await db.Assets.FindAsync(assetId))!.CurrentValue; });
        return value;
    }

    private static Task SeedAsync(
        BackendWebApplicationFactory factory,
        Guid owner,
        Guid? member,
        Guid? outside,
        Guid space,
        IReadOnlyCollection<Asset> assets) => factory.SeedAsync(async db =>
    {
        db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"OWNER-{owner:N}@EXAMPLE.COM", DisplayName = "Owner", IsActive = true });
        if (member.HasValue) db.Users.Add(new FullWorthUser { Id = member.Value, EmailNormalized = $"MEMBER-{member:N}@EXAMPLE.COM", DisplayName = "Member", IsActive = true });
        if (outside.HasValue) db.Users.Add(new FullWorthUser { Id = outside.Value, EmailNormalized = $"OUTSIDE-{outside:N}@EXAMPLE.COM", DisplayName = "Outside", IsActive = true });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Specialized", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
        if (member.HasValue) db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member.Value, Role = FullWorthSpaceRoles.Member });
        db.Assets.AddRange(assets);
        await db.SaveChangesAsync();
    });

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
