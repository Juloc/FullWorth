using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class IncomeDetectionWorkflowTests
{
    [Fact]
    public async Task AcceptedCandidateIsNotSuggestedAgain()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid(); var account = Guid.NewGuid();
        await SeedRecurringIncome(factory, owner, FullWorthSpaceRoles.Owner, account, AccountOwnershipTypes.Owner);

        var candidate = await GetSingleCandidate(client, owner);
        using var accept = UserRequest(HttpMethod.Post,
            $"/api/income-schedules/detection/accept?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        accept.Content = JsonContent.Create(new
        {
            accountId = candidate.GetProperty("accountId").GetGuid(),
            counterparty = candidate.GetProperty("counterparty").GetString(),
            typicalAmount = candidate.GetProperty("typicalAmount").GetDecimal(),
            currency = candidate.GetProperty("currency").GetString(),
            cycle = candidate.GetProperty("cycle").GetString(),
            nextExpectedDate = candidate.GetProperty("nextExpectedDate").GetString(),
            confidence = candidate.GetProperty("confidence").GetDecimal(),
            occurrences = candidate.GetProperty("occurrences").GetInt32()
        });
        using var accepted = await client.SendAsync(accept);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var after = await GetCandidates(client, owner);
        Assert.Empty(after.EnumerateArray());
    }

    [Fact]
    public async Task DismissedCandidateIsNotSuggestedAgain()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid(); var account = Guid.NewGuid();
        await SeedRecurringIncome(factory, owner, FullWorthSpaceRoles.Owner, account, AccountOwnershipTypes.Owner);

        var candidate = await GetSingleCandidate(client, owner);
        using var dismiss = UserRequest(HttpMethod.Post,
            $"/api/income-schedules/detection/dismiss?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        dismiss.Content = JsonContent.Create(new
        {
            accountId = candidate.GetProperty("accountId").GetGuid(),
            counterparty = candidate.GetProperty("counterparty").GetString(),
            currency = candidate.GetProperty("currency").GetString(),
            cycle = candidate.GetProperty("cycle").GetString()
        });
        using var dismissed = await client.SendAsync(dismiss);
        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);

        var after = await GetCandidates(client, owner);
        Assert.Empty(after.EnumerateArray());
    }

    [Fact]
    public async Task EditorCannotDismissCandidateOnViewerOnlyAccount()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var editor = Guid.NewGuid(); var account = Guid.NewGuid();
        await SeedRecurringIncome(factory, editor, FullWorthSpaceRoles.Member, account, AccountOwnershipTypes.Viewer);
        await factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{editor},{"budgets.manage"},{true},{DateTimeOffset.UtcNow})
ON CONFLICT ("FullWorthSpaceId","UserId","Capability") DO UPDATE SET "IsAllowed"=true,"UpdatedAt"={DateTimeOffset.UtcNow}
"""));

        var candidate = await GetSingleCandidate(client, editor);
        using var dismiss = UserRequest(HttpMethod.Post,
            $"/api/income-schedules/detection/dismiss?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", editor);
        dismiss.Content = JsonContent.Create(new
        {
            accountId = candidate.GetProperty("accountId").GetGuid(),
            counterparty = candidate.GetProperty("counterparty").GetString(),
            currency = candidate.GetProperty("currency").GetString(),
            cycle = candidate.GetProperty("cycle").GetString()
        });
        using var response = await client.SendAsync(dismiss);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var after = await GetCandidates(client, editor);
        Assert.Single(after.EnumerateArray());
    }

    private static async Task<JsonElement> GetSingleCandidate(HttpClient client, Guid user)
    {
        var items = await GetCandidates(client, user);
        var array = items.EnumerateArray().ToArray();
        Assert.Single(array);
        return array[0].Clone();
    }

    private static async Task<JsonElement> GetCandidates(HttpClient client, Guid user)
    {
        using var request = UserRequest(HttpMethod.Get,
            $"/api/income-schedules/detection?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private static async Task SeedRecurringIncome(
        BackendWebApplicationFactory factory, Guid user, string role, Guid account, string ownership)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Income detection user",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = role
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"income-{account:N}",
                ProviderAccountId = $"income-{account:N}",
                InstitutionName = "Manual",
                DisplayName = "Income account",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = user,
                OwnershipType = ownership
            });

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var daysAgo in new[] { 90, 60, 30 })
            {
                var date = today.AddDays(-daysAgo);
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = account,
                    ExternalKey = $"salary-{daysAgo}-{Guid.NewGuid():N}",
                    Status = "BOOK",
                    BookingDate = date,
                    ValueDate = date,
                    Amount = 2500m,
                    Currency = "EUR",
                    Counterparty = "Example Employer",
                    NormalizedCounterparty = "EXAMPLE EMPLOYER",
                    CategorizationSource = "none",
                    RawJson = "{}"
                });
            }
            await db.SaveChangesAsync();
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
