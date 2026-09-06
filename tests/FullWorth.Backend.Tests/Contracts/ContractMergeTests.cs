using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Contracts;

public sealed class ContractMergeTests
{
    [Fact]
    public async Task Merge_HidesDuplicateButKeepsAccountAndPaymentHistory_AndCanBeUndone()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var merge = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/{s.Target}/merge?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { sourceContractIds = new[] { s.Source } }));
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);

        using var listResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var rows = await listResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        var visible = Assert.Single(rows!);
        Assert.Equal(s.Target, visible.GetProperty("id").GetGuid());

        using var sourcesResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts/{s.Target}/merged-sources?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.OK, sourcesResponse.StatusCode);
        var sources = await sourcesResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        var source = Assert.Single(sources!);
        Assert.Equal(s.Source, source.GetProperty("id").GetGuid());
        Assert.Equal(s.AccountB, source.GetProperty("accountId").GetGuid());

        using var activityResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts/{s.Target}/activity?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.OK, activityResponse.StatusCode);
        using var activity = JsonDocument.Parse(await activityResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, activity.RootElement.GetProperty("matchedCount").GetInt32());

        using var unmerge = await client.SendAsync(Request(
            HttpMethod.Delete,
            $"/api/contracts/{s.Target}/merge/{s.Source}?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.NoContent, unmerge.StatusCode);

        using var listAfter = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.OK, listAfter.StatusCode);
        var rowsAfter = await listAfter.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.Equal(2, rowsAfter!.Count);
    }

    [Theory]
    [InlineData("WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG", "WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG")]
    [InlineData("MÜLLER GMBH", "MUELLER GMBH")]
    public void ContractIdentity_NormalizesGermanSpellingVariants(string left, string right)
        => Assert.Equal(ContractIdentity.Normalize(left), ContractIdentity.Normalize(right));

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = s.Owner,
                EmailNormalized = $"{s.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Merge owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = s.Space,
                Name = "Merge",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = s.Space,
                UserId = s.Owner,
                Role = FullWorthSpaceRoles.Owner
            });

            db.BankConnections.Add(new BankConnection
            {
                Id = s.Connection,
                FullWorthSpaceId = s.Space,
                Provider = "test",
                InstitutionName = "Merge Bank",
                Country = "DE",
                ProviderSessionId = $"merge-{s.Connection:N}",
                Status = "AUTHORIZED"
            });

            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = s.AccountA,
                    FullWorthSpaceId = s.Space,
                    BankConnectionId = s.Connection,
                    Provider = "test",
                    IdentificationHash = $"merge-{s.AccountA:N}",
                    ProviderAccountId = $"provider-{s.AccountA:N}",
                    InstitutionName = "Merge Bank",
                    DisplayName = "Old account",
                    Currency = "EUR"
                },
                new FinanceAccount
                {
                    Id = s.AccountB,
                    FullWorthSpaceId = s.Space,
                    BankConnectionId = s.Connection,
                    Provider = "test",
                    IdentificationHash = $"merge-{s.AccountB:N}",
                    ProviderAccountId = $"provider-{s.AccountB:N}",
                    InstitutionName = "Merge Bank",
                    DisplayName = "New account",
                    Currency = "EUR"
                });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = s.AccountA, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = s.AccountB, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });

            db.Contracts.AddRange(
                new RecurringContract
                {
                    Id = s.Target,
                    FullWorthSpaceId = s.Space,
                    Name = "WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG",
                    ProviderName = "WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG",
                    AccountId = s.AccountA,
                    Amount = 182m,
                    Currency = "EUR",
                    BillingCycle = "monthly",
                    IsActive = true
                },
                new RecurringContract
                {
                    Id = s.Source,
                    FullWorthSpaceId = s.Space,
                    Name = "WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG",
                    ProviderName = "WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG",
                    AccountId = s.AccountB,
                    Amount = 182m,
                    Currency = "EUR",
                    BillingCycle = "monthly",
                    IsActive = true
                });

            db.Transactions.AddRange(
                new FinanceTransaction
                {
                    AccountId = s.AccountA,
                    ExternalKey = "old-weg",
                    Amount = -182m,
                    Currency = "EUR",
                    Counterparty = "WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG",
                    NormalizedCounterparty = "weg am koenigstraessle 1 5 vertr d ppg",
                    BookingDate = new DateOnly(2026, 7, 1),
                    CategorizationSource = "none"
                },
                new FinanceTransaction
                {
                    AccountId = s.AccountB,
                    ExternalKey = "new-weg",
                    Amount = -182m,
                    Currency = "EUR",
                    Counterparty = "WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG",
                    NormalizedCounterparty = "weg am königsträßle 1 5 vertr d ppg",
                    BookingDate = new DateOnly(2026, 8, 1),
                    CategorizationSource = "none"
                });

            await db.SaveChangesAsync();
        });

        return s;
    }

    private sealed record Scenario(
        Guid Owner,
        Guid Space,
        Guid Connection,
        Guid AccountA,
        Guid AccountB,
        Guid Target,
        Guid Source);
}
