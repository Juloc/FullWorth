using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FullWorth.Backend.Tests.Transactions;

public sealed class TransferDetectionTests
{
    private static readonly DateOnly Day = new(2026, 8, 1);

    [Fact]
    public void PairsEqualAndOppositeAcrossAccountsWithinWindow()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();
        var candidates = new[]
        {
            new TransferCandidate(a, acctA, -50m, "EUR", Day),
            new TransferCandidate(b, acctB, 50m, "EUR", Day.AddDays(1))
        };

        var pairs = TransferDetectionService.FindMutualUniquePairs(candidates, windowDays: 3);
        Assert.Single(pairs);
    }

    [Fact]
    public void AutomaticPairsRequireOwnedAccountIdentifierMatch()
    {
        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();
        var a = new TransferCandidate(Guid.NewGuid(), acctA, -50m, "EUR", Day, "iban-a", "iban-b");
        var b = new TransferCandidate(Guid.NewGuid(), acctB, 50m, "EUR", Day.AddDays(1), "iban-b", "iban-a");

        Assert.Single(TransferDetectionService.FindAutomaticPairs([a, b], 3));

        var amountOnlyA = a with { CounterpartyAccountLookup = null };
        var amountOnlyB = b with { CounterpartyAccountLookup = null };
        Assert.Empty(TransferDetectionService.FindAutomaticPairs([amountOnlyA, amountOnlyB], 3));
        Assert.Single(TransferDetectionService.FindMutualUniquePairs([amountOnlyA, amountOnlyB], 3));
    }

    [Fact]
    public void AmbiguousAndInvalidCandidatesAreLeftUnpaired()
    {
        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();
        var acctC = Guid.NewGuid();

        // One +50 with two possible -50 counterparts -> ambiguous -> no pair.
        var ambiguous = new[]
        {
            new TransferCandidate(Guid.NewGuid(), acctA, 50m, "EUR", Day),
            new TransferCandidate(Guid.NewGuid(), acctB, -50m, "EUR", Day),
            new TransferCandidate(Guid.NewGuid(), acctC, -50m, "EUR", Day)
        };
        Assert.Empty(TransferDetectionService.FindMutualUniquePairs(ambiguous, 3));

        // Same account, out of window, and different currency each fail to pair.
        var invalid = new[]
        {
            new TransferCandidate(Guid.NewGuid(), acctA, 50m, "EUR", Day),
            new TransferCandidate(Guid.NewGuid(), acctA, -50m, "EUR", Day),          // same account
            new TransferCandidate(Guid.NewGuid(), acctB, -50m, "EUR", Day.AddDays(10)), // out of window
            new TransferCandidate(Guid.NewGuid(), acctB, -50m, "USD", Day)           // different currency
        };
        Assert.Empty(TransferDetectionService.FindMutualUniquePairs(invalid, 3));
    }

    [Fact]
    public async Task DetectPreviewsThenLinksTransfersAndProtectsNonMatches()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var preview = await client.SendAsync(Request($"/api/transfers/detect?fullWorthSpaceId={s.Space}&apply=false", s.Owner));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewSummary = await preview.Content.ReadFromJsonAsync<SummaryView>();
        Assert.Equal(3, previewSummary!.Evaluated);
        Assert.Equal(1, previewSummary.PairsLinked);
        Assert.False(previewSummary.Applied);

        await factory.SeedAsync(async db =>
            Assert.Null(await db.Transactions.AsNoTracking().Where(x => x.Id == s.TxOut).Select(x => x.TransferGroupId).SingleAsync()));

        using var apply = await client.SendAsync(Request($"/api/transfers/detect?fullWorthSpaceId={s.Space}&apply=true", s.Owner));
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        var applySummary = await apply.Content.ReadFromJsonAsync<SummaryView>();
        Assert.Equal(1, applySummary!.PairsLinked);
        Assert.True(applySummary.Applied);

        await factory.SeedAsync(async db =>
        {
            var outLeg = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.TxOut);
            var inLeg = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.TxIn);
            Assert.NotNull(outLeg.TransferGroupId);
            Assert.Equal(outLeg.TransferGroupId, inLeg.TransferGroupId);
            Assert.True(outLeg.IsTransfer && inLeg.IsTransfer);

            var unrelated = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.TxUnrelated);
            Assert.Null(unrelated.TransferGroupId);
        });
    }

    [Fact]
    public async Task NonOwnerCannotDetect()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request($"/api/transfers/detect?fullWorthSpaceId={s.Space}&apply=true", s.Member));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedFullWorthUserAsync(s.Owner);
        await factory.SeedFullWorthUserAsync(s.Member);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var connectionId = Guid.NewGuid();
            var acctA = Guid.NewGuid();
            var acctB = Guid.NewGuid();

            db.Set<FullWorthSpace>().Add(new FullWorthSpace { Id = s.Space, Name = "Space", BaseCurrency = "EUR", CreatedAt = now, UpdatedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner, JoinedAt = now });
            db.Set<FullWorthSpaceMember>().Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Member, Role = FullWorthSpaceRoles.Member, JoinedAt = now });

            db.Set<BankConnection>().Add(new BankConnection { Id = connectionId, FullWorthSpaceId = s.Space, InstitutionName = "Bank" });
            db.Set<FinanceAccount>().Add(new FinanceAccount { Id = acctA, FullWorthSpaceId = s.Space, BankConnectionId = connectionId, IdentificationHash = "hA", ProviderAccountId = "pA", InstitutionName = "Bank", DisplayName = "A", IbanLookup = "iban-a" });
            db.Set<FinanceAccount>().Add(new FinanceAccount { Id = acctB, FullWorthSpaceId = s.Space, BankConnectionId = connectionId, IdentificationHash = "hB", ProviderAccountId = "pB", InstitutionName = "Bank", DisplayName = "B", IbanLookup = "iban-b" });

            db.Set<FinanceTransaction>().Add(new FinanceTransaction { Id = s.TxOut, AccountId = acctA, ExternalKey = "out", Amount = -200m, Currency = "EUR", BookingDate = Day, CounterpartyAccountLookup = "iban-b" });
            db.Set<FinanceTransaction>().Add(new FinanceTransaction { Id = s.TxIn, AccountId = acctB, ExternalKey = "in", Amount = 200m, Currency = "EUR", BookingDate = Day.AddDays(1), CounterpartyAccountLookup = "iban-a" });
            db.Set<FinanceTransaction>().Add(new FinanceTransaction { Id = s.TxUnrelated, AccountId = acctA, ExternalKey = "u", Amount = -13.37m, Currency = "EUR", BookingDate = Day });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Scenario(Guid Space, Guid Owner, Guid Member, Guid TxOut, Guid TxIn, Guid TxUnrelated);
    private sealed record SummaryView(int Evaluated, int PairsLinked, bool Applied);
}
