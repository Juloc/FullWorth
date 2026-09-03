using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Categories;

public sealed class RuleReapplyTests
{
    [Fact]
    public async Task PreviewReportsChangesWithoutApplying()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/categorization-rules/reapply?fullWorthSpaceId={s.Space}&apply=false", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ReapplyView>();
        Assert.NotNull(result);
        Assert.False(result!.Applied);
        Assert.Equal(2, result.Evaluated);   // the two non-manual transactions
        Assert.Equal(1, result.Changed);      // only the ACME one would change

        // Nothing was persisted.
        await factory.SeedAsync(async db =>
        {
            var match = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.TxMatch);
            Assert.Null(match.CategoryId);
            Assert.Equal("none", match.CategorizationSource);
        });
    }

    [Fact]
    public async Task ApplyCategorizesMatchesButProtectsManual()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/categorization-rules/reapply?fullWorthSpaceId={s.Space}&apply=true", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ReapplyView>();
        Assert.True(result!.Applied);
        Assert.Equal(1, result.Changed);

        await factory.SeedAsync(async db =>
        {
            var match = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.TxMatch);
            Assert.Equal(s.CatRule, match.CategoryId);
            Assert.Equal("rule", match.CategorizationSource);

            var manual = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.TxManual);
            Assert.Equal(s.CatManual, manual.CategoryId);
            Assert.Equal("manual", manual.CategorizationSource);

            var noMatch = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.TxNoMatch);
            Assert.Null(noMatch.CategoryId);
        });
    }

    [Fact]
    public async Task PreviewMatchesDraftRuleAgainstAllTransactionsWithoutSaving()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var draft = new
        {
            name = "",
            isEnabled = true,
            priority = 100,
            target = "transaction",
            matchField = "counterparty",
            matchMode = "contains",
            pattern = "ACME",
            direction = "any",
            minAmount = (decimal?)null,
            maxAmount = (decimal?)null,
            merchantCategoryCode = (string?)null,
            categoryId = Guid.Empty,
            markAsTransfer = false,
            stopProcessing = false
        };
        using var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/categorization-rules/preview?fullWorthSpaceId={s.Space}", s.Owner, draft));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PreviewView>();
        Assert.NotNull(result);
        Assert.Equal(3, result!.Evaluated);                  // all three transactions are scanned
        Assert.Equal(2, result.Matched);                     // both ACME rows match, manual state is ignored for preview
        Assert.Equal(2, result.Sample.Count);
        Assert.All(result.Sample, x => Assert.Contains("ACME", x.Label));

        // Preview must not have created a rule.
        await factory.SeedAsync(async db =>
            Assert.Equal(1, await db.CategorizationRules.AsNoTracking().CountAsync(x => x.FullWorthSpaceId == s.Space)));
    }

    [Fact]
    public async Task PreviewRejectsADraftWithNoConditions()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var empty = new
        {
            name = "", isEnabled = true, priority = 100, target = "transaction",
            matchField = "any", matchMode = "contains", pattern = "",
            direction = "any", minAmount = (decimal?)null, maxAmount = (decimal?)null,
            merchantCategoryCode = (string?)null, categoryId = Guid.Empty, markAsTransfer = false, stopProcessing = false
        };
        using var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/categorization-rules/preview?fullWorthSpaceId={s.Space}", s.Owner, empty));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonOwnerCannotPreview()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var draft = new
        {
            name = "", isEnabled = true, priority = 100, target = "transaction",
            matchField = "counterparty", matchMode = "contains", pattern = "ACME",
            direction = "any", minAmount = (decimal?)null, maxAmount = (decimal?)null,
            merchantCategoryCode = (string?)null, categoryId = Guid.Empty, markAsTransfer = false, stopProcessing = false
        };
        using var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/categorization-rules/preview?fullWorthSpaceId={s.Space}", s.Member, draft));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NonOwnerCannotReapply()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(HttpMethod.Post, $"/api/categorization-rules/reapply?fullWorthSpaceId={s.Space}&apply=true", s.Member));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedFullWorthUserAsync(s.Owner);
        await factory.SeedFullWorthUserAsync(s.Member);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var connectionId = Guid.NewGuid();
            var accountId = Guid.NewGuid();

            db.Set<FullWorthSpace>().Add(new FullWorthSpace { Id = s.Space, Name = "Space", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Member, Role = FullWorthSpaceRoles.Member, JoinedAt = now });

            db.Set<BankConnection>().Add(new BankConnection { Id = connectionId, FullWorthSpaceId = s.Space, InstitutionName = "Bank" });
            db.Set<FinanceAccount>().Add(new FinanceAccount { Id = accountId, FullWorthSpaceId = s.Space, BankConnectionId = connectionId, IdentificationHash = "hash1", ProviderAccountId = "p1", InstitutionName = "Bank", DisplayName = "Checking" });
            // The owner writes every account: global rule reapply requires full writable-account coverage
            // so it never recategorizes transactions in accounts the caller cannot see.
            db.Set<AccountOwner>().Add(new AccountOwner { AccountId = accountId, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });

            db.Set<FinanceCategory>().Add(new FinanceCategory { Id = s.CatRule, FullWorthSpaceId = s.Space, Key = "food", Name = "Food" });
            db.Set<FinanceCategory>().Add(new FinanceCategory { Id = s.CatManual, FullWorthSpaceId = s.Space, Key = "shopping", Name = "Shopping" });

            db.Set<CategorizationRule>().Add(new CategorizationRule
            {
                FullWorthSpaceId = s.Space, Name = "acme", IsEnabled = true, Priority = 100,
                Target = "transaction", MatchField = "counterparty", MatchMode = "contains",
                Pattern = "ACME", Direction = "any", CategoryId = s.CatRule, StopProcessing = true
            });

            db.Set<FinanceTransaction>().Add(new FinanceTransaction { Id = s.TxMatch, AccountId = accountId, ExternalKey = "k1", Amount = -12.5m, Counterparty = "ACME MARKET", CategorizationSource = "none" });
            db.Set<FinanceTransaction>().Add(new FinanceTransaction { Id = s.TxManual, AccountId = accountId, ExternalKey = "k2", Amount = -9m, Counterparty = "ACME MARKET", CategoryId = s.CatManual, CategorizationSource = "manual" });
            db.Set<FinanceTransaction>().Add(new FinanceTransaction { Id = s.TxNoMatch, AccountId = accountId, ExternalKey = "k3", Amount = -4m, Counterparty = "LOCAL BAKERY", CategorizationSource = "none" });

            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scenario(
        Guid Space, Guid Owner, Guid Member, Guid CatRule, Guid CatManual,
        Guid TxMatch, Guid TxManual, Guid TxNoMatch);

    private sealed record ReapplyView(int Evaluated, int Changed, bool Applied);
    private sealed record PreviewView(int Evaluated, int Matched, bool ScanCapped, List<PreviewMatchView> Sample);
    private sealed record PreviewMatchView(Guid Id, string? Date, string Label, decimal Amount, string Currency, string? CurrentCategory);
}
