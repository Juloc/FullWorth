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
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Contracts;

/// <summary>
/// The reported case: one utility contract that changed its paying account twice, so three contract rows
/// exist for one agreement - and one of those rows carries no currency at all, which used to make the
/// merge unreachable from both the candidate list and every server-side check.
/// </summary>
public sealed class ContractMultiMergeTests
{
    [Fact]
    public async Task MergePreview_TreatsAMissingCurrencyAsCompatible()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.WithoutCurrency, s.Middle, s.Newest } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var preview = json.RootElement;
        Assert.Equal(3, preview.GetProperty("contracts").GetArrayLength());
        Assert.Equal(3, preview.GetProperty("accountIds").GetArrayLength());

        // The survivor without a currency reports the only code the selection knows, and says where it
        // came from - the preview must not invent a currency silently.
        var currency = preview.GetProperty("canonicalFields").EnumerateArray()
            .Single(field => field.GetProperty("field").GetString() == "currency");
        Assert.Equal("EUR", currency.GetProperty("value").GetString());
    }

    [Fact]
    public async Task MergeExecute_KeepsTheChosenSurvivorWithEveryPaymentAccountAndPayment()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var ids = new[] { s.WithoutCurrency, s.Middle, s.Newest };

        // The default ordering would keep the newest-paying contract; the owner picks the oldest row -
        // the one still carrying the customer data - so the chosen survivor has to win.
        using var previewResponse = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = ids, preferredCanonicalContractId = s.WithoutCurrency }));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        Assert.Equal(s.WithoutCurrency, previewJson.RootElement.GetProperty("canonicalContractId").GetGuid());
        var token = previewJson.RootElement.GetProperty("previewToken").GetString();

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = ids, canonicalContractId = s.WithoutCurrency, previewToken = token }));
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
        using var executed = JsonDocument.Parse(await execute.Content.ReadAsStringAsync());
        Assert.Equal(s.WithoutCurrency, executed.RootElement.GetProperty("canonicalContractId").GetGuid());
        Assert.Equal(2, executed.RootElement.GetProperty("mergedContractIds").GetArrayLength());

        using var listResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={s.Space}",
            s.Owner));
        var rows = await listResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        var visible = Assert.Single(rows!);
        Assert.Equal(s.WithoutCurrency, visible.GetProperty("id").GetGuid());

        // Payment-account history: both former accounts stay reachable from the survivor.
        using var sourcesResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts/{s.WithoutCurrency}/merged-sources?fullWorthSpaceId={s.Space}",
            s.Owner));
        var sources = await sourcesResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.Equal(
            new[] { s.AccountB, s.AccountC }.OrderBy(id => id).ToArray(),
            sources!.Select(source => source.GetProperty("accountId").GetGuid()).OrderBy(id => id).ToArray());

        // All three bookings still count towards the one contract. This only works because the survivor
        // adopted the currency of the selection - the payment match filters bookings by it.
        using var activityResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts/{s.WithoutCurrency}/activity?fullWorthSpaceId={s.Space}",
            s.Owner));
        using var activity = JsonDocument.Parse(await activityResponse.Content.ReadAsStringAsync());
        Assert.Equal("EUR", activity.RootElement.GetProperty("currency").GetString());
        Assert.Equal(3, activity.RootElement.GetProperty("matchedCount").GetInt32());

        // Nothing is deleted by a merge: the merged-away rows and every transaction are still there.
        await factory.SeedAsync(async db =>
        {
            var contracts = await db.Contracts.AsNoTracking()
                .Where(contract => contract.FullWorthSpaceId == s.Space)
                .ToListAsync();
            Assert.Equal(3, contracts.Count);
            Assert.Equal(2, contracts.Count(contract => contract.MergedIntoContractId == s.WithoutCurrency));
            Assert.Equal("EUR", contracts.Single(contract => contract.Id == s.WithoutCurrency).Currency);
            Assert.Equal(3, await db.Transactions.CountAsync(transaction =>
                transaction.AccountId == s.AccountA ||
                transaction.AccountId == s.AccountB ||
                transaction.AccountId == s.AccountC));
        });
    }

    [Fact]
    public async Task MergePreview_RefusesTwoKnownCurrenciesAndNamesThem()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await factory.SeedAsync(async db =>
        {
            var contract = await db.Contracts.SingleAsync(item => item.Id == s.Newest);
            contract.Currency = "USD";
            await db.SaveChangesAsync();
        });

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.WithoutCurrency, s.Middle, s.Newest } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("EUR", error, StringComparison.Ordinal);
        Assert.Contains("USD", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParityMerge_AlsoAcceptsAContractWithoutACurrency()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contract-parity/merge?fullWorthSpaceId={s.Space}",
            s.Owner,
            new
            {
                contractIds = new[] { s.Middle, s.WithoutCurrency },
                targetContractId = s.Middle
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(new[] { "EUR", "", "eur" }, true, "EUR")]
    [InlineData(new[] { "", "" }, true, "")]
    [InlineData(new[] { "EUR", "USD" }, false, "")]
    public void ContractMergeCurrency_OnlyTwoKnownCodesAreAConflict(
        string[] currencies,
        bool expectedCompatible,
        string expectedResolved)
    {
        var compatible = ContractMergeCurrency.TryResolve(currencies, out var resolved);

        Assert.Equal(expectedCompatible, compatible);
        Assert.Equal(expectedResolved, resolved);
    }

    [Fact]
    public void ContractsList_OffersMultiSelectMergeAndDoesNotFilterCandidatesByAnExactCurrency()
    {
        var js = File.ReadAllText(Path.Combine(
            Root(), "src", "FullWorth.Web", "wwwroot", "features", "contracts.js"));

        // The old candidate filter compared the raw currency strings, which excluded every contract
        // without one.
        Assert.DoesNotContain("(candidate.currency || '') === (contract.currency || '')", js);
        Assert.Contains("mergeCurrencyCompatible(candidate, contract)", js);

        // Multi-select in the list, and one chosen survivor per merge.
        Assert.Contains("data-select-toggle", js);
        Assert.Contains("data-selection-merge", js);
        Assert.Contains("data-select-row", js);
        Assert.Contains("canonicalContractId", js);
        Assert.Contains("preferredCanonicalContractId", js);

        // Merging goes through the preview/execute pair, never a second merge implementation.
        Assert.Contains("api/contracts/merge-preview", js);
        Assert.Contains("api/contracts/merge-execute", js);
    }

    [Fact]
    public void ContractsMergeCss_IsAFeatureStylesheetWithoutHardcodedColours()
    {
        var css = File.ReadAllText(Path.Combine(
            Root(), "src", "FullWorth.Web", "wwwroot", "styles", "features", "contracts-merge.css"));

        Assert.Contains(".contracts-selection", css);
        Assert.Contains(".contract-merge-survivor", css);
        Assert.DoesNotContain("#", css);

        var js = File.ReadAllText(Path.Combine(
            Root(), "src", "FullWorth.Web", "wwwroot", "features", "contracts.js"));
        Assert.Contains("/styles/features/contracts-merge.css", js);
    }

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
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = s.Owner,
                EmailNormalized = $"{s.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Multi merge owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Weg", BaseCurrency = "EUR" });
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
                InstitutionName = "Weg Bank",
                Country = "DE",
                ProviderSessionId = $"weg-{s.Connection:N}",
                Status = "AUTHORIZED"
            });

            foreach (var (accountId, name) in new[]
                     {
                         (s.AccountA, "Oldest account"),
                         (s.AccountB, "Middle account"),
                         (s.AccountC, "Newest account")
                     })
            {
                db.Accounts.Add(new FinanceAccount
                {
                    Id = accountId,
                    FullWorthSpaceId = s.Space,
                    BankConnectionId = s.Connection,
                    Provider = "test",
                    IdentificationHash = $"weg-{accountId:N}",
                    ProviderAccountId = $"provider-{accountId:N}",
                    InstitutionName = "Weg Bank",
                    DisplayName = name,
                    Currency = "EUR"
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = accountId,
                    UserId = s.Owner,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
            }

            // The same agreement three times. The oldest row has no currency at all - the shape a
            // contract created before the write path required a code arrives in.
            db.Contracts.AddRange(
                new RecurringContract
                {
                    Id = s.WithoutCurrency,
                    FullWorthSpaceId = s.Space,
                    Name = "WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG",
                    ProviderName = "WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG",
                    AccountId = s.AccountA,
                    Amount = 182m,
                    Currency = string.Empty,
                    BillingCycle = "monthly",
                    AutoDetected = true,
                    IsActive = true
                },
                new RecurringContract
                {
                    Id = s.Middle,
                    FullWorthSpaceId = s.Space,
                    Name = "WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG",
                    ProviderName = "WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG",
                    AccountId = s.AccountB,
                    Amount = 182m,
                    Currency = "EUR",
                    BillingCycle = "monthly",
                    AutoDetected = true,
                    IsActive = true
                },
                new RecurringContract
                {
                    Id = s.Newest,
                    FullWorthSpaceId = s.Space,
                    Name = "WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG",
                    ProviderName = "WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG",
                    AccountId = s.AccountC,
                    Amount = 182m,
                    Currency = "EUR",
                    BillingCycle = "monthly",
                    AutoDetected = true,
                    IsActive = true
                });

            var month = 6;
            foreach (var accountId in new[] { s.AccountA, s.AccountB, s.AccountC })
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = accountId,
                    ExternalKey = $"weg-{accountId:N}",
                    Amount = -182m,
                    Currency = "EUR",
                    Counterparty = "WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG",
                    NormalizedCounterparty = "weg am königsträßle 1 5 vertr d ppg",
                    BookingDate = new DateOnly(2026, month++, 1),
                    CategorizationSource = "none"
                });
            }

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
        Guid AccountC,
        Guid WithoutCurrency,
        Guid Middle,
        Guid Newest);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
