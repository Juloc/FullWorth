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

public sealed class RealEstateRentalCashflowIntegrationTests
{
    [Fact]
    public async Task LeaseIsPlannedOnlyAndTransactionBackedRentDrivesActualMetricsWithoutDoubleAllocation()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var property = Guid.NewGuid();
        var otherAsset = Guid.NewGuid();
        var account = Guid.NewGuid();
        var hiddenAccount = Guid.NewGuid();
        var rentTransaction = Guid.NewGuid();
        var hiddenTransaction = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"RENT-{owner:N}@EXAMPLE.COM", DisplayName = "Rent owner", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Rental property", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner });
            db.Assets.AddRange(
                new Asset { Id = property, FullWorthSpaceId = space, Name = "Eigentumswohnung", Kind = AssetKinds.RealEstate, CurrentValue = 300_000m, Currency = "EUR", IncludeInNetWorth = true },
                new Asset { Id = otherAsset, FullWorthSpaceId = space, Name = "Other asset", Kind = AssetKinds.Other, CurrentValue = 1_000m, Currency = "EUR", IncludeInNetWorth = true });
            db.Accounts.AddRange(
                Account(account, space, "Rent account"),
                Account(hiddenAccount, space, "Hidden account"));
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.AddRange(
                new FinanceTransaction { Id = rentTransaction, AccountId = account, ExternalKey = "rent:1", Status = "BOOK", BookingDate = new DateOnly(2026, 6, 1), Amount = 1_500m, Currency = "EUR", Counterparty = "Mieter", RawJson = "{}" },
                new FinanceTransaction { Id = hiddenTransaction, AccountId = hiddenAccount, ExternalKey = "hidden:1", Status = "BOOK", BookingDate = new DateOnly(2026, 6, 1), Amount = 500m, Currency = "EUR", Counterparty = "Hidden", RawJson = "{}" });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Put, $"/api/assets/{property}/real-estate?fullWorthSpaceId={space}", owner,
            new { propertyType = "apartment", usageType = "rented", countryCode = "DE", ownershipSharePercent = 100m, livingAreaSqm = 80m }))).StatusCode);

        using var unitResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/real-estate/units?fullWorthSpaceId={space}", owner,
            new { name = "Wohnung", unitType = "apartment", areaSqm = 80m, rooms = 3m, ownershipSharePercent = 100m, isOwnerOccupied = false, isActive = true, notes = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, unitResponse.StatusCode);
        using var unitJson = JsonDocument.Parse(await unitResponse.Content.ReadAsStringAsync());
        var unit = unitJson.RootElement.GetProperty("id").GetGuid();

        using var leaseResponse = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/real-estate/leases?fullWorthSpaceId={space}", owner,
            new { propertyUnitId = unit, tenantDisplayLabel = "Mieter", startDate = "2026-01-01", endDate = (string?)null, status = "active", coldRent = 1_000m, utilitiesAdvance = 200m, otherRecurringCharges = 0m, currency = "EUR", paymentCycle = "monthly", depositAmount = 3_000m, depositHeld = true, lastRentChangeDate = (string?)null, nextReviewDate = (string?)null, notes = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, leaseResponse.StatusCode);

        using var overlapping = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/real-estate/leases?fullWorthSpaceId={space}", owner,
            new { propertyUnitId = unit, tenantDisplayLabel = "Second", startDate = "2026-07-01", endDate = (string?)null, status = "active", coldRent = 900m, utilitiesAdvance = 0m, otherRecurringCharges = 0m, currency = "EUR", paymentCycle = "monthly", depositAmount = (decimal?)null, depositHeld = (bool?)null, lastRentChangeDate = (string?)null, nextReviewDate = (string?)null, notes = (string?)null }));
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);

        var before = await MetricsAsync(client, property, space, owner);
        Assert.Equal(12_000m, before.GetProperty("annualColdRent").GetDecimal());
        Assert.Equal(0m, before.GetProperty("actualRent").GetDecimal());

        using var rentCashflow = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", owner,
            new { transactionId = rentTransaction, date = "2020-01-01", type = "rental_income", amount = 1_200m, direction = "expense", currency = "USD", isPlanned = false, notes = "June rent" }));
        Assert.Equal(HttpStatusCode.OK, rentCashflow.StatusCode);
        using var rentJson = JsonDocument.Parse(await rentCashflow.Content.ReadAsStringAsync());
        Assert.Equal("2026-06-01", rentJson.RootElement.GetProperty("date").GetString());
        Assert.Equal("income", rentJson.RootElement.GetProperty("direction").GetString());
        Assert.Equal("EUR", rentJson.RootElement.GetProperty("currency").GetString());

        using var overAllocation = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{otherAsset}/cashflows?fullWorthSpaceId={space}", owner,
            new { transactionId = rentTransaction, date = (string?)null, type = "income", amount = 400m, direction = "income", currency = "EUR", isPlanned = false, notes = (string?)null }));
        Assert.Equal(HttpStatusCode.BadRequest, overAllocation.StatusCode);

        using var hidden = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", owner,
            new { transactionId = hiddenTransaction, date = (string?)null, type = "rental_income", amount = 100m, direction = "income", currency = "EUR", isPlanned = false, notes = (string?)null }));
        Assert.Equal(HttpStatusCode.BadRequest, hidden.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", owner,
            new { transactionId = (Guid?)null, date = "2026-06-15", type = "operating_expense", amount = 300m, direction = "expense", currency = "EUR", isPlanned = false, notes = "Non recoverable" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", owner,
            new { transactionId = (Guid?)null, date = "2026-06-30", type = "debt_payment", amount = 700m, direction = "expense", currency = "EUR", isPlanned = false, notes = "Mortgage payment" }))).StatusCode);

        var metrics = await MetricsAsync(client, property, space, owner);
        Assert.Equal(12_000m, metrics.GetProperty("annualColdRent").GetDecimal());
        Assert.Equal(1_200m, metrics.GetProperty("actualRent").GetDecimal());
        Assert.Equal(300m, metrics.GetProperty("nonRecoverableOperatingCosts").GetDecimal());
        Assert.Equal(900m, metrics.GetProperty("netOperatingIncome").GetDecimal());
        Assert.Equal(700m, metrics.GetProperty("debtPayments").GetDecimal());
        Assert.Equal(200m, metrics.GetProperty("cashflowBeforeTax").GetDecimal());
        Assert.Equal(0.04m, metrics.GetProperty("grossYield").GetDecimal());
        Assert.Equal(0.039m, metrics.GetProperty("netRentalYield").GetDecimal());
    }

    [Fact]
    public async Task MemberCanReadSharedAssetCashflowButCannotSeeTransactionReferenceFromUnsharedAccount()
    {
        using var factory = new BackendWebApplicationFactory();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        var space = Guid.NewGuid();
        var property = Guid.NewGuid();
        var account = Guid.NewGuid();
        var transaction = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = owner, EmailNormalized = $"OWNER-{owner:N}@EXAMPLE.COM", DisplayName = "Owner", IsActive = true },
                new FullWorthUser { Id = member, EmailNormalized = $"MEMBER-{member:N}@EXAMPLE.COM", DisplayName = "Member", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Shared property", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = space, UserId = member, Role = FullWorthSpaceRoles.Member });
            db.Assets.Add(new Asset { Id = property, FullWorthSpaceId = space, Name = "Shared property", Kind = AssetKinds.RealEstate, CurrentValue = 250_000m, Currency = "EUR", IncludeInNetWorth = true });
            db.Accounts.Add(Account(account, space, "Owner-only rent account"));
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction { Id = transaction, AccountId = account, ExternalKey = "rent:private", Status = "BOOK", BookingDate = new DateOnly(2026, 8, 1), Amount = 900m, Currency = "EUR", Counterparty = "Private tenant name", RawJson = "{}" });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var create = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", owner,
            new { transactionId = transaction, date = (string?)null, type = "rental_income", amount = 900m, direction = "income", currency = "EUR", isPlanned = false, notes = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using var ownerList = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", owner));
        Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
        using var ownerJson = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync());
        var ownerRow = ownerJson.RootElement.EnumerateArray().Single();
        Assert.Equal(transaction, ownerRow.GetProperty("transactionId").GetGuid());
        Assert.Equal("Private tenant name", ownerRow.GetProperty("transactionCounterparty").GetString());

        using var memberList = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{property}/cashflows?fullWorthSpaceId={space}", member));
        Assert.Equal(HttpStatusCode.OK, memberList.StatusCode);
        using var memberJson = JsonDocument.Parse(await memberList.Content.ReadAsStringAsync());
        var memberRow = memberJson.RootElement.EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, memberRow.GetProperty("transactionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, memberRow.GetProperty("transactionCounterparty").ValueKind);
        Assert.Equal(900m, memberRow.GetProperty("amount").GetDecimal());
        Assert.Equal("rental_income", memberRow.GetProperty("type").GetString());
    }

    private static async Task<JsonElement> MetricsAsync(HttpClient client, Guid asset, Guid space, Guid owner)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{asset}/real-estate/metrics?fullWorthSpaceId={space}&from=2026-01-01&to=2026-12-31", owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static FinanceAccount Account(Guid id, Guid space, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = space,
        Provider = "manual",
        IdentificationHash = $"test-{id:N}",
        ProviderAccountId = $"test-{id:N}",
        InstitutionName = "Test",
        DisplayName = name,
        Currency = "EUR",
        IsActive = true,
        IncludeInNetWorth = true
    };

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
