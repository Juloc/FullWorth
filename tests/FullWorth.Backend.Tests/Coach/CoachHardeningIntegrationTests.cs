using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Coach;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FullWorth.Backend.Tests.Coach;

public sealed class CoachHardeningIntegrationTests
{
    [Fact]
    public async Task ReviewCrudAndValidationAreUserScoped()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory, withSecondUser: true);
        using var client = factory.CreateClient();

        using var first = await PutReview(client, seed.UserId, seed.SpaceId, seed.TransactionId, "Positive", ["good_value"], "Worth it");
        using var second = await PutReview(client, seed.SecondUserId!.Value, seed.SpaceId, seed.TransactionId, "Negative", ["poor_value"], "Regretted");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using var firstGet = Request(HttpMethod.Get, ReviewUri(seed), seed.UserId);
        using var firstResult = await client.SendAsync(firstGet);
        using var secondGet = Request(HttpMethod.Get, ReviewUri(seed), seed.SecondUserId.Value);
        using var secondResult = await client.SendAsync(secondGet);
        using var firstJson = JsonDocument.Parse(await firstResult.Content.ReadAsStringAsync());
        using var secondJson = JsonDocument.Parse(await secondResult.Content.ReadAsStringAsync());
        Assert.Equal("Positive", firstJson.RootElement.GetProperty("sentiment").GetString());
        Assert.Equal("Negative", secondJson.RootElement.GetProperty("sentiment").GetString());

        using var updated = await PutReview(client, seed.UserId, seed.SpaceId, seed.TransactionId, "Negative", ["impulse"], "Updated");
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var oversized = await PutReview(client, seed.UserId, seed.SpaceId, seed.TransactionId, "Negative", ["impulse"], new string('x', 501));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        using var delete = Request(HttpMethod.Delete, ReviewUri(seed), seed.UserId);
        using var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missingRequest = Request(HttpMethod.Get, ReviewUri(seed), seed.UserId);
        using var missing = await client.SendAsync(missingRequest);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var otherStillThereRequest = Request(HttpMethod.Get, ReviewUri(seed), seed.SecondUserId.Value);
        using var otherStillThere = await client.SendAsync(otherStillThereRequest);
        Assert.Equal(HttpStatusCode.OK, otherStillThere.StatusCode);
    }

    [Fact]
    public async Task ZeroReviewsHaveNullScoreAndTransfersAreExcluded()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var request = Request(HttpMethod.Get, $"/api/spending-reviews/summary?fullWorthSpaceId={seed.SpaceId}", seed.UserId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("worthItScore").ValueKind);
        Assert.Equal(0m, json.RootElement.GetProperty("reviewedOutgoingAmount").GetDecimal());
        Assert.Equal(100m, json.RootElement.GetProperty("totalOutgoingAmount").GetDecimal());
    }

    [Fact]
    public async Task ConversationOwnershipAndPromptLimitAreEnforced()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory, withSecondUser: true);
        using var client = factory.CreateClient();
        var conversationId = await CreateConversation(client, seed.UserId, seed.SpaceId);

        using var otherGet = Request(HttpMethod.Get, $"/api/coach/conversations/{conversationId}?fullWorthSpaceId={seed.SpaceId}", seed.SecondUserId!.Value);
        using var otherResult = await client.SendAsync(otherGet);
        Assert.Equal(HttpStatusCode.NotFound, otherResult.StatusCode);

        using var tooLong = Request(HttpMethod.Post, $"/api/coach/conversations/{conversationId}/messages?fullWorthSpaceId={seed.SpaceId}", seed.UserId,
            JsonContent.Create(new { text = new string('x', 2001) }));
        using var tooLongResult = await client.SendAsync(tooLong);
        Assert.Equal(HttpStatusCode.BadRequest, tooLongResult.StatusCode);
    }

    [Fact]
    public async Task StartingNewConversationLeavesOnlyOneActiveChat()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var firstId = await CreateConversation(client, seed.UserId, seed.SpaceId);
        var secondId = await CreateConversation(client, seed.UserId, seed.SpaceId);
        Assert.NotEqual(firstId, secondId);

        using var oldRequest = Request(HttpMethod.Get, $"/api/coach/conversations/{firstId}?fullWorthSpaceId={seed.SpaceId}", seed.UserId);
        using var oldResponse = await client.SendAsync(oldRequest);
        Assert.Equal(HttpStatusCode.NotFound, oldResponse.StatusCode);

        using var listRequest = Request(HttpMethod.Get, $"/api/coach/conversations?fullWorthSpaceId={seed.SpaceId}&limit=20", seed.UserId);
        using var listResponse = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var json = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var conversations = json.RootElement.EnumerateArray().ToArray();
        Assert.Single(conversations);
        Assert.Equal(secondId, conversations[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CurrentPageContextIsPassedSeparatelyToCoachProvider()
    {
        var provider = new CapturingProvider();
        using var factory = FactoryWithProvider(provider);
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var conversationId = await CreateConversation(client, seed.UserId, seed.SpaceId);

        using var ask = Request(HttpMethod.Post, $"/api/coach/conversations/{conversationId}/messages?fullWorthSpaceId={seed.SpaceId}", seed.UserId,
            JsonContent.Create(new
            {
                text = "Was fällt dir hier auf?",
                uiContext = new
                {
                    page = "transactions",
                    title = "Buchungen",
                    path = "/transactions?accountId=abc",
                    filters = new Dictionary<string, string>
                    {
                        ["accountId"] = "abc",
                        ["direction"] = "expense",
                        ["notAllowed"] = "ignore-me"
                    },
                    entityType = "transaction",
                    entityId = "tx-123",
                    entityLabel = "REWE 42,17 €",
                    details = new Dictionary<string, string>
                    {
                        ["amount"] = "42.17",
                        ["currency"] = "EUR",
                        ["note"] = "must-be-dropped"
                    },
                    selectedIds = Enumerable.Range(1, 25).Select(i => $"tx-{i}").ToArray(),
                    selectedItems = Enumerable.Range(1, 25).Select(i => new
                    {
                        id = $"tx-{i}",
                        label = $"Transaction {i}",
                        details = new Dictionary<string, string>
                        {
                            ["amount"] = i.ToString(),
                            ["currency"] = "EUR",
                            ["note"] = "must-be-dropped"
                        }
                    }).ToArray()
                }
            }));
        using var response = await client.SendAsync(ask);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(provider.LastRequest?.UiContext);
        Assert.Equal("transactions", provider.LastRequest!.UiContext!.Page);
        Assert.Equal("Buchungen", provider.LastRequest.UiContext.Title);
        Assert.Equal("expense", provider.LastRequest.UiContext.Filters!["direction"]);
        Assert.DoesNotContain("notAllowed", provider.LastRequest.UiContext.Filters.Keys);
        Assert.Equal("transaction", provider.LastRequest.UiContext.EntityType);
        Assert.Equal("tx-123", provider.LastRequest.UiContext.EntityId);
        Assert.Equal("REWE 42,17 €", provider.LastRequest.UiContext.EntityLabel);
        Assert.Equal("42.17", provider.LastRequest.UiContext.Details!["amount"]);
        Assert.Equal("EUR", provider.LastRequest.UiContext.Details["currency"]);
        Assert.DoesNotContain("note", provider.LastRequest.UiContext.Details.Keys);
        Assert.Equal(20, provider.LastRequest.UiContext.SelectedIds!.Count);
        Assert.Equal(20, provider.LastRequest.UiContext.SelectedItems!.Count);
        Assert.Equal("tx-1", provider.LastRequest.UiContext.SelectedItems[0].Id);
        Assert.Equal("1", provider.LastRequest.UiContext.SelectedItems[0].Details!["amount"]);
        Assert.DoesNotContain("note", provider.LastRequest.UiContext.SelectedItems[0].Details.Keys);

        using var reload = Request(HttpMethod.Get, $"/api/coach/conversations/{conversationId}?fullWorthSpaceId={seed.SpaceId}", seed.UserId);
        using var reloaded = await client.SendAsync(reload);
        using var json = JsonDocument.Parse(await reloaded.Content.ReadAsStringAsync());
        var userMessage = json.RootElement.GetProperty("messages").EnumerateArray().First();
        Assert.Equal("Was fällt dir hier auf?", userMessage.GetProperty("text").GetString());
        Assert.DoesNotContain("Buchungen", userMessage.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ProviderFailureFallsBackToDeterministicAnswer()
    {
        var provider = new CapturingProvider { Throw = true };
        using var factory = FactoryWithProvider(provider);
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var ask = Request(HttpMethod.Post, $"/api/coach/ask?fullWorthSpaceId={seed.SpaceId}", seed.UserId,
            JsonContent.Create(new { text = "Wo ist mein Geld hin?" }));
        using var response = await client.SendAsync(ask);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Deterministic", json.RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task RequestedModelIsPassedToCoachProviderAndReturned()
    {
        var provider = new CapturingProvider();
        using var factory = FactoryWithProvider(provider);
        var seed = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var ask = Request(HttpMethod.Post, $"/api/coach/ask?fullWorthSpaceId={seed.SpaceId}", seed.UserId,
            JsonContent.Create(new { text = "Analysiere meine Ausgaben.", model = "gpt-coach-test" }));
        using var response = await client.SendAsync(ask);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("gpt-coach-test", provider.LastRequest?.Model);
        Assert.Equal("gpt-coach-test", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProviderPayloadExcludesReviewNotesAndOtherFullWorthSpaces()
    {
        var provider = new CapturingProvider();
        using var factory = FactoryWithProvider(provider);
        var seed = await SeedAsync(factory);
        await SeedOtherSpaceAsync(factory, seed.UserId);
        using var client = factory.CreateClient();

        const string privateNote = "PRIVATE-REVIEW-NOTE-MUST-NOT-LEAVE-BACKEND";
        using var review = await PutReview(client, seed.UserId, seed.SpaceId, seed.TransactionId, "Negative", ["impulse"], privateNote);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);

        using var ask = Request(HttpMethod.Post, $"/api/coach/ask?fullWorthSpaceId={seed.SpaceId}", seed.UserId,
            JsonContent.Create(new { text = "Was habe ich bereut?" }));
        using var response = await client.SendAsync(ask);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Ai", json.RootElement.GetProperty("mode").GetString());

        Assert.NotNull(provider.LastRequest);
        var serialized = JsonSerializer.Serialize(provider.LastRequest);
        Assert.DoesNotContain(privateNote, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Other Space Only", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("9876", serialized, StringComparison.Ordinal);
        Assert.Contains("Shared Wallet", serialized, StringComparison.Ordinal);
        Assert.Contains("Coach Internet", serialized, StringComparison.Ordinal);
        Assert.Equal(100m, provider.LastRequest!.Context.Outgoing);
    }

    private static BackendWebApplicationFactory FactoryWithProvider(CapturingProvider provider)
    {
        var resolver = new StaticResolver(provider);
        return new BackendWebApplicationFactory(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<ICoachProviderResolver>();
            services.AddSingleton<ICoachProviderResolver>(resolver);
        });
    }

    private static async Task<SeedData> SeedAsync(BackendWebApplicationFactory factory, bool withSecondUser = false)
    {
        var userId = Guid.NewGuid();
        var secondUserId = withSecondUser ? Guid.NewGuid() : (Guid?)null;
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = userId, EmailNormalized = $"{userId:N}@EXAMPLE.COM", DisplayName = "Coach Owner", IsActive = true });
            if (secondUserId.HasValue)
                db.Users.Add(new FullWorthUser { Id = secondUserId.Value, EmailNormalized = $"{secondUserId.Value:N}@EXAMPLE.COM", DisplayName = "Coach Member", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Coach", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
            if (secondUserId.HasValue)
                db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = secondUserId.Value, Role = FullWorthSpaceRoles.Member });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = $"coach-hardening-{accountId:N}",
                ProviderAccountId = $"coach-hardening-{accountId:N}",
                InstitutionName = "Manual",
                DisplayName = "Shared Wallet",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
            if (secondUserId.HasValue)
                db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = secondUserId.Value, OwnershipType = AccountOwnershipTypes.Owner });
            db.Contracts.Add(new RecurringContract
            {
                FullWorthSpaceId = spaceId,
                Name = "Coach Internet",
                ProviderName = "ISP",
                Kind = "internet",
                AccountId = accountId,
                Amount = 49.99m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                IsActive = true
            });
            db.Transactions.AddRange(
                new FinanceTransaction
                {
                    Id = transactionId,
                    AccountId = accountId,
                    ExternalKey = "coach-hardening-expense",
                    Amount = -100m,
                    Currency = "EUR",
                    BookingDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Counterparty = "Local Shop"
                },
                new FinanceTransaction
                {
                    AccountId = accountId,
                    ExternalKey = "coach-hardening-income",
                    Amount = 500m,
                    Currency = "EUR",
                    BookingDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Counterparty = "Employer"
                },
                new FinanceTransaction
                {
                    AccountId = accountId,
                    ExternalKey = "coach-hardening-transfer",
                    Amount = -999m,
                    Currency = "EUR",
                    BookingDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Counterparty = "Own account",
                    IsTransfer = true
                });
            await db.SaveChangesAsync();
        });
        return new(userId, secondUserId, spaceId, transactionId);
    }

    private static Task SeedOtherSpaceAsync(BackendWebApplicationFactory factory, Guid userId) => factory.SeedAsync(async db =>
    {
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Other", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = spaceId, UserId = userId, Role = FullWorthSpaceRoles.Owner });
        db.Accounts.Add(new FinanceAccount
        {
            Id = accountId,
            FullWorthSpaceId = spaceId,
            Provider = "manual",
            IdentificationHash = $"other-{accountId:N}",
            ProviderAccountId = $"other-{accountId:N}",
            InstitutionName = "Manual",
            DisplayName = "Other",
            Currency = "EUR",
            IsActive = true,
            IncludeInNetWorth = true
        });
        db.AccountOwners.Add(new AccountOwner { AccountId = accountId, UserId = userId, OwnershipType = AccountOwnershipTypes.Owner });
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = accountId,
            ExternalKey = "other-space-secret-amount",
            Amount = -9876m,
            Currency = "EUR",
            BookingDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Counterparty = "Other Space Only"
        });
        await db.SaveChangesAsync();
    });

    private static async Task<Guid> CreateConversation(HttpClient client, Guid userId, Guid spaceId)
    {
        using var create = Request(HttpMethod.Post, $"/api/coach/conversations?fullWorthSpaceId={spaceId}", userId,
            JsonContent.Create(new { title = (string?)null, mascotId = (string?)null }));
        using var response = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static string ReviewUri(SeedData seed) => $"/api/spending-reviews/transactions/{seed.TransactionId}?fullWorthSpaceId={seed.SpaceId}";

    private static HttpRequestMessage Request(HttpMethod method, string uri, Guid userId, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static Task<HttpResponseMessage> PutReview(HttpClient client, Guid userId, Guid spaceId, Guid transactionId, string sentiment, string[] reasons, string? note)
    {
        var request = Request(HttpMethod.Put, $"/api/spending-reviews/transactions/{transactionId}?fullWorthSpaceId={spaceId}", userId,
            JsonContent.Create(new { sentiment, reasons, note }));
        return client.SendAsync(request);
    }

    private sealed record SeedData(Guid UserId, Guid? SecondUserId, Guid SpaceId, Guid TransactionId);

    private sealed class StaticResolver(ICoachTextProvider provider) : ICoachProviderResolver
    {
        public Task<ICoachTextProvider?> ResolveAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken cancellationToken) => Task.FromResult<ICoachTextProvider?>(provider);
    }

    private sealed class CapturingProvider : ICoachTextProvider
    {
        public string ProviderId => "test-provider";
        public bool Throw { get; init; }
        public CoachProviderRequest? LastRequest { get; private set; }

        public Task<CoachProviderResult> CompleteAsync(CoachProviderRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (Throw) throw new InvalidOperationException("provider unavailable");
            return Task.FromResult(new CoachProviderResult("Provider answer based only on supplied facts.", [], [], request.Model));
        }
    }
}
