using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Coach;

public sealed class CoachAndSpendingReviewIntegrationTests
{
    [Fact]
    public async Task ReviewSummaryIsAmountWeightedAndUsesRealCoverage()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var positiveResponse = await PutReview(client, seed.UserId, seed.SpaceId, seed.PositiveTransactionId, "Positive", ["good_value"]);
        using var negativeResponse = await PutReview(client, seed.UserId, seed.SpaceId, seed.NegativeTransactionId, "Negative", ["impulse"]);
        Assert.Equal(HttpStatusCode.OK, positiveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, negativeResponse.StatusCode);

        using var request = Request(HttpMethod.Get, $"/api/spending-reviews/summary?fullWorthSpaceId={seed.SpaceId}", seed.UserId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.False(root.GetProperty("incomplete").GetBoolean());
        Assert.Equal(200m, root.GetProperty("totalOutgoingAmount").GetDecimal());
        Assert.Equal(150m, root.GetProperty("reviewedOutgoingAmount").GetDecimal());
        Assert.Equal(.75m, root.GetProperty("reviewCoverage").GetDecimal());
        Assert.Equal(100m, root.GetProperty("positiveAmount").GetDecimal());
        Assert.Equal(50m, root.GetProperty("negativeAmount").GetDecimal());
        Assert.Equal(1m / 3m, root.GetProperty("worthItScore").GetDecimal());

        var category = Assert.Single(root.GetProperty("categories").EnumerateArray().ToArray());
        Assert.Equal(200m, category.GetProperty("totalOutgoingAmount").GetDecimal());
        Assert.Equal(150m, category.GetProperty("reviewedAmount").GetDecimal());
        Assert.Equal(.75m, category.GetProperty("reviewCoverage").GetDecimal());
    }

    [Fact]
    public async Task ReviewSummaryConvertsForeignCurrencyAtHistoricalRate()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await factory.SeedAsync(async db =>
        {
            db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 2m });
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = seed.AccountId,
                ExternalKey = "coach-usd-expense",
                Amount = -200m,
                Currency = "USD",
                BookingDate = today,
                Counterparty = "USD Shop"
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, $"/api/spending-reviews/summary?fullWorthSpaceId={seed.SpaceId}", seed.UserId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(json.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(300m, json.RootElement.GetProperty("totalOutgoingAmount").GetDecimal());
    }

    [Fact]
    public async Task ReviewSummaryMarksMissingFxRateIncompleteInsteadOfAssumingOneToOne()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = seed.AccountId,
                ExternalKey = "coach-jpy-expense",
                Amount = -10_000m,
                Currency = "JPY",
                BookingDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Counterparty = "JPY Shop"
            });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, $"/api/spending-reviews/summary?fullWorthSpaceId={seed.SpaceId}", seed.UserId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(json.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.Equal(200m, json.RootElement.GetProperty("totalOutgoingAmount").GetDecimal());
    }

    [Fact]
    public async Task ReviewCannotCrossFullWorthSpaceBoundary()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        var otherSpace = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = otherSpace, Name = "Other", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = otherSpace, UserId = seed.UserId, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var response = await PutReview(client, seed.UserId, otherSpace, seed.PositiveTransactionId, "Positive", []);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReviewRejectsReasonThatDoesNotMatchSentiment()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await PutReview(client, seed.UserId, seed.SpaceId, seed.PositiveTransactionId, "Positive", ["impulse"]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CoachConversationWorksWithDeterministicFallbackAndKeepsEvidence()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = Request(HttpMethod.Post, $"/api/coach/conversations?fullWorthSpaceId={seed.SpaceId}", seed.UserId,
            JsonContent.Create(new { title = (string?)null, mascotId = "raccoon" }));
        using var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var conversationId = createdJson.RootElement.GetProperty("id").GetGuid();

        using var ask = Request(HttpMethod.Post, $"/api/coach/conversations/{conversationId}/messages?fullWorthSpaceId={seed.SpaceId}", seed.UserId,
            JsonContent.Create(new { text = "Wo ist mein Geld hin?" }));
        using var answer = await client.SendAsync(ask);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("Assistant", message.GetProperty("role").GetString());
        Assert.Equal("Deterministic", message.GetProperty("mode").GetString());
        Assert.Contains("200", message.GetProperty("text").GetString());
        Assert.NotEmpty(message.GetProperty("facts").EnumerateArray().ToArray());
        Assert.NotEmpty(json.RootElement.GetProperty("followUps").EnumerateArray().ToArray());

        using var reload = Request(HttpMethod.Get, $"/api/coach/conversations/{conversationId}?fullWorthSpaceId={seed.SpaceId}", seed.UserId);
        using var reloaded = await client.SendAsync(reload);
        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        using var reloadedJson = JsonDocument.Parse(await reloaded.Content.ReadAsStringAsync());
        var assistant = reloadedJson.RootElement.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("Assistant", assistant.GetProperty("role").GetString());
        Assert.NotEmpty(assistant.GetProperty("facts").EnumerateArray().ToArray());
    }

    private static async Task<SeedData> SeedAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var positiveId = Guid.NewGuid();
        var negativeId = Guid.NewGuid();
        var unreviewedId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Coach User", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Coach Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = $"coach-{accountId:N}",
                ProviderAccountId = $"coach-{accountId:N}",
                InstitutionName = "Manual",
                DisplayName = "Wallet",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.Add(new FinanceCategory { Id = categoryId, FullWorthSpaceId = spaceId, Key = "coach-shopping", Name = "Shopping" });
            db.Transactions.AddRange(
                new FinanceTransaction { Id = positiveId, AccountId = accountId, ExternalKey = "coach-positive", Amount = -100m, Currency = "EUR", BookingDate = today, CategoryId = categoryId, Counterparty = "Good Shop" },
                new FinanceTransaction { Id = negativeId, AccountId = accountId, ExternalKey = "coach-negative", Amount = -50m, Currency = "EUR", BookingDate = today, CategoryId = categoryId, Counterparty = "Impulse Shop" },
                new FinanceTransaction { Id = unreviewedId, AccountId = accountId, ExternalKey = "coach-unreviewed", Amount = -50m, Currency = "EUR", BookingDate = today, CategoryId = categoryId, Counterparty = "Other Shop" },
                new FinanceTransaction { AccountId = accountId, ExternalKey = "coach-income", Amount = 500m, Currency = "EUR", BookingDate = today, Counterparty = "Employer" },
                new FinanceTransaction { AccountId = accountId, ExternalKey = "coach-transfer", Amount = -999m, Currency = "EUR", BookingDate = today, Counterparty = "Own account", IsTransfer = true });
            await db.SaveChangesAsync();
        });

        return new(userId, spaceId, accountId, positiveId, negativeId, unreviewedId);
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static Task<HttpResponseMessage> PutReview(HttpClient client, Guid userId, Guid spaceId, Guid transactionId, string sentiment, string[] reasons)
    {
        var request = Request(HttpMethod.Put, $"/api/spending-reviews/transactions/{transactionId}?fullWorthSpaceId={spaceId}", userId,
            JsonContent.Create(new { sentiment, reasons, note = (string?)null }));
        return client.SendAsync(request);
    }

    private sealed record SeedData(Guid UserId, Guid SpaceId, Guid AccountId, Guid PositiveTransactionId, Guid NegativeTransactionId, Guid UnreviewedTransactionId);
}
