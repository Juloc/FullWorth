using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Actions;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class ActionProposalApiTests
{
    [Fact]
    public async Task CategoryChange_PreviewsWithoutMutation_ThenExecutesAuditsAndRetriesIdempotently()
    {
        using var factory = ActionsOnFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateProposalAsync(client, s, new
        {
            handler = ActionProposalHandlerNames.TransactionCategoryChange,
            payload = new { transactionId = s.Expense, categoryId = s.FoodCategory },
            source = "manual"
        });

        var proposal = created.GetProperty("proposal");
        var proposalId = proposal.GetProperty("id").GetGuid();
        var token = proposal.GetProperty("previewToken").GetString()!;
        Assert.Equal("pending", proposal.GetProperty("state").GetString());
        Assert.Equal(s.FoodCategory, proposal.GetProperty("preview").GetProperty("targetCategoryId").GetGuid());

        await factory.SeedAsync(async db =>
        {
            var transaction = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.Expense);
            Assert.Null(transaction.CategoryId);
        });

        using (var execute = await client.SendAsync(Request(
                   HttpMethod.Post,
                   $"/api/action-proposals/{proposalId}/execute?fullWorthSpaceId={s.Space}",
                   s.Owner,
                   new { previewToken = token })))
        {
            Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
            using var json = JsonDocument.Parse(await execute.Content.ReadAsStringAsync());
            Assert.Equal("executed", json.RootElement.GetProperty("proposal").GetProperty("state").GetString());
            Assert.False(json.RootElement.GetProperty("alreadyApplied").GetBoolean());
        }

        await factory.SeedAsync(async db =>
        {
            var transaction = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.Expense);
            Assert.Equal(s.FoodCategory, transaction.CategoryId);
            Assert.Equal("manual", transaction.CategorizationSource);

            var audit = await db.Set<AuditEvent>().AsNoTracking().SingleAsync(x =>
                x.EntityId == proposalId &&
                x.Action == "autopilot.action.executed");
            Assert.Equal(s.Owner, audit.ActorUserId);
            Assert.Equal(s.Space, audit.FullWorthSpaceId);
        });

        using var retry = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals/{proposalId}/execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { previewToken = token }));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.True(retryJson.RootElement.GetProperty("alreadyApplied").GetBoolean());
    }

    [Fact]
    public async Task Execute_RefreshesPreviewAndConflictsWhenTransactionChanged()
    {
        using var factory = ActionsOnFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateProposalAsync(client, s, new
        {
            handler = ActionProposalHandlerNames.TransactionCategoryChange,
            payload = new { transactionId = s.Expense, categoryId = s.FoodCategory },
            source = "manual"
        });
        var proposal = created.GetProperty("proposal");
        var proposalId = proposal.GetProperty("id").GetGuid();
        var oldToken = proposal.GetProperty("previewToken").GetString()!;

        await factory.SeedAsync(async db =>
        {
            var transaction = await db.Transactions.SingleAsync(x => x.Id == s.Expense);
            transaction.UserNote = "changed after preview";
            transaction.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync();
        });

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals/{proposalId}/execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { previewToken = oldToken }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var refreshed = json.RootElement.GetProperty("proposal");
        Assert.NotEqual(oldToken, refreshed.GetProperty("previewToken").GetString());
        Assert.Equal("pending", refreshed.GetProperty("state").GetString());

        await factory.SeedAsync(async db =>
        {
            var transaction = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.Expense);
            Assert.Null(transaction.CategoryId);
        });
    }

    [Fact]
    public async Task RejectedProposalCannotExecute_AndUnknownHandlerIsRejected()
    {
        using var factory = ActionsOnFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using (var unknown = await client.SendAsync(Request(
                   HttpMethod.Post,
                   $"/api/action-proposals?fullWorthSpaceId={s.Space}",
                   s.Owner,
                   new { handler = "arbitrary-url-handler", payload = new { url = "/api/transactions" } })))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var created = await CreateProposalAsync(client, s, new
        {
            handler = ActionProposalHandlerNames.TransactionCategoryChange,
            payload = new { transactionId = s.Expense, categoryId = s.FoodCategory }
        });
        var proposal = created.GetProperty("proposal");
        var proposalId = proposal.GetProperty("id").GetGuid();
        var token = proposal.GetProperty("previewToken").GetString()!;

        using (var reject = await client.SendAsync(Request(
                   HttpMethod.Post,
                   $"/api/action-proposals/{proposalId}/reject?fullWorthSpaceId={s.Space}",
                   s.Owner)))
        {
            Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
            using var json = JsonDocument.Parse(await reject.Content.ReadAsStringAsync());
            Assert.Equal("rejected", json.RootElement.GetProperty("proposal").GetProperty("state").GetString());
        }

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals/{proposalId}/execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { previewToken = token }));
        Assert.Equal(HttpStatusCode.Conflict, execute.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyMemberCannotCreateTransactionActionProposal()
    {
        using var factory = ActionsOnFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var viewer = Guid.NewGuid();

        await factory.SeedFullWorthUserAsync(viewer);
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = s.Space,
                UserId = viewer,
                Role = FullWorthSpaceRoles.Member
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = s.AccountA,
                UserId = viewer,
                OwnershipType = AccountOwnershipTypes.Viewer
            });
            await db.SaveChangesAsync();
        });

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals?fullWorthSpaceId={s.Space}",
            viewer,
            new
            {
                handler = ActionProposalHandlerNames.TransactionCategoryChange,
                payload = new { transactionId = s.Expense, categoryId = s.FoodCategory }
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ActionsKillSwitchHidesProposalApi()
    {
        using var factory = new BackendWebApplicationFactory(new Dictionary<string, string?>
        {
            [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Actions}"] = "off"
        });
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals?fullWorthSpaceId={s.Space}",
            s.Owner,
            new
            {
                handler = ActionProposalHandlerNames.TransactionCategoryChange,
                payload = new { transactionId = s.Expense, categoryId = s.FoodCategory }
            }));
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);

        using var list = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/action-proposals?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }

    [Fact]
    public async Task TransferLinkProposal_UsesExistingTransferStoreAndIsIdempotent()
    {
        using var factory = ActionsOnFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateProposalAsync(client, s, new
        {
            handler = ActionProposalHandlerNames.TransferLink,
            payload = new
            {
                firstTransactionId = s.TransferOut,
                secondTransactionId = s.TransferIn
            },
            source = "insight",
            sourceReference = "transfer-signal-1"
        });
        var proposal = created.GetProperty("proposal");
        var proposalId = proposal.GetProperty("id").GetGuid();
        var token = proposal.GetProperty("previewToken").GetString()!;
        Assert.Equal(-125m, proposal.GetProperty("preview").GetProperty("first").GetProperty("amount").GetDecimal());

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals/{proposalId}/execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { previewToken = token }));
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var rows = await db.Transactions.AsNoTracking()
                .Where(x => x.Id == s.TransferOut || x.Id == s.TransferIn)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.True(row.IsTransfer));
            Assert.True(rows[0].TransferGroupId.HasValue);
            Assert.Equal(rows[0].TransferGroupId, rows[1].TransferGroupId);
        });

        // Re-opening the same Insight action reuses/reconciles rather than creating a second pending write.
        var second = await CreateProposalAsync(client, s, new
        {
            handler = ActionProposalHandlerNames.TransferLink,
            payload = new
            {
                firstTransactionId = s.TransferOut,
                secondTransactionId = s.TransferIn
            },
            source = "insight",
            sourceReference = "transfer-signal-1"
        });
        Assert.True(second.GetProperty("alreadyApplied").GetBoolean());
    }

    [Fact]
    public async Task RuleCreateProposal_UsesProposalIdAsStableRuleId()
    {
        using var factory = ActionsOnFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var created = await CreateProposalAsync(client, s, new
        {
            handler = ActionProposalHandlerNames.CategorizationRuleUpsert,
            payload = new
            {
                ruleId = (Guid?)null,
                rule = new
                {
                    name = "ACME food",
                    isEnabled = true,
                    priority = 100,
                    target = "transaction",
                    matchField = "counterparty",
                    matchMode = "contains",
                    pattern = "ACME",
                    direction = "expense",
                    minAmount = (decimal?)null,
                    maxAmount = (decimal?)null,
                    merchantCategoryCode = (string?)null,
                    categoryId = s.FoodCategory,
                    markAsTransfer = false,
                    stopProcessing = true
                }
            }
        });

        var proposal = created.GetProperty("proposal");
        var proposalId = proposal.GetProperty("id").GetGuid();
        var token = proposal.GetProperty("previewToken").GetString()!;
        Assert.True(proposal.GetProperty("preview").GetProperty("matched").GetInt32() >= 1);

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals/{proposalId}/execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { previewToken = token }));
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var rule = await db.CategorizationRules.AsNoTracking().SingleAsync(x => x.Id == proposalId);
            Assert.Equal("ACME food", rule.Name);
            Assert.Equal(s.FoodCategory, rule.CategoryId);
        });

        using var retry = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals/{proposalId}/execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { previewToken = token }));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await factory.SeedAsync(async db =>
            Assert.Equal(1, await db.CategorizationRules.AsNoTracking().CountAsync(x => x.Id == proposalId)));
    }

    private static BackendWebApplicationFactory ActionsOnFactory() =>
        new(new Dictionary<string, string?>
        {
            [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Actions}"] = "on"
        });

    private static async Task<JsonElement> CreateProposalAsync(HttpClient client, Scenario s, object body)
    {
        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/action-proposals?fullWorthSpaceId={s.Space}",
            s.Owner,
            body));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());

        await factory.SeedFullWorthUserAsync(s.Owner);
        await factory.SeedAsync(async db =>
        {
            var connection = new BankConnection
            {
                Id = s.Connection,
                FullWorthSpaceId = s.Space,
                Provider = "test",
                InstitutionName = "Proposal Bank",
                Country = "DE",
                ProviderSessionId = $"proposal-{s.Connection:N}",
                Status = "AUTHORIZED"
            };
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = s.Space,
                Name = "Proposal space",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = s.Space,
                UserId = s.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(connection);
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = s.AccountA,
                    FullWorthSpaceId = s.Space,
                    BankConnectionId = s.Connection,
                    Provider = "test",
                    IdentificationHash = $"proposal-{s.AccountA:N}",
                    ProviderAccountId = $"provider-{s.AccountA:N}",
                    InstitutionName = "Proposal Bank",
                    DisplayName = "Checking",
                    Currency = "EUR"
                },
                new FinanceAccount
                {
                    Id = s.AccountB,
                    FullWorthSpaceId = s.Space,
                    BankConnectionId = s.Connection,
                    Provider = "test",
                    IdentificationHash = $"proposal-{s.AccountB:N}",
                    ProviderAccountId = $"provider-{s.AccountB:N}",
                    InstitutionName = "Proposal Bank",
                    DisplayName = "Savings",
                    Currency = "EUR"
                });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = s.AccountA, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = s.AccountB, UserId = s.Owner, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.Add(new FinanceCategory
            {
                Id = s.FoodCategory,
                FullWorthSpaceId = s.Space,
                Key = "food",
                Name = "Food"
            });
            db.Transactions.AddRange(
                new FinanceTransaction
                {
                    Id = s.Expense,
                    AccountId = s.AccountA,
                    ExternalKey = "proposal-expense",
                    Amount = -42m,
                    Currency = "EUR",
                    Counterparty = "ACME MARKET",
                    NormalizedCounterparty = "acme market",
                    BookingDate = new DateOnly(2026, 9, 1),
                    CategorizationSource = "none"
                },
                new FinanceTransaction
                {
                    Id = s.TransferOut,
                    AccountId = s.AccountA,
                    ExternalKey = "proposal-transfer-out",
                    Amount = -125m,
                    Currency = "EUR",
                    Counterparty = "Own transfer",
                    BookingDate = new DateOnly(2026, 9, 2),
                    CategorizationSource = "none"
                },
                new FinanceTransaction
                {
                    Id = s.TransferIn,
                    AccountId = s.AccountB,
                    ExternalKey = "proposal-transfer-in",
                    Amount = 125m,
                    Currency = "EUR",
                    Counterparty = "Own transfer",
                    BookingDate = new DateOnly(2026, 9, 2),
                    CategorizationSource = "none"
                });
            await db.SaveChangesAsync();
        });
        return s;
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        string path,
        Guid userId,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scenario(
        Guid Owner,
        Guid Space,
        Guid Connection,
        Guid AccountA,
        Guid AccountB,
        Guid FoodCategory,
        Guid Expense,
        Guid TransferOut,
        Guid TransferIn);
}
