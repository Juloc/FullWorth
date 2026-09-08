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

public sealed class ContractMergeTests
{
    [Fact]
    public async Task MergeExecute_UsesPreviewToken_AndRetryIsIdempotent()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var previewResponse = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source } }));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var preview = previewJson.RootElement;
        var canonical = preview.GetProperty("canonicalContractId").GetGuid();
        var token = preview.GetProperty("previewToken").GetString();
        Assert.True(preview.GetProperty("executionEnabled").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(token));

        var body = new
        {
            contractIds = new[] { s.Target, s.Source },
            canonicalContractId = canonical,
            previewToken = token
        };

        using (var execute = await client.SendAsync(Request(
                   HttpMethod.Post,
                   $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
                   s.Owner,
                   body)))
        {
            Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
            using var result = JsonDocument.Parse(await execute.Content.ReadAsStringAsync());
            Assert.False(result.RootElement.GetProperty("alreadyApplied").GetBoolean());
            Assert.Equal(canonical, result.RootElement.GetProperty("canonicalContractId").GetGuid());
        }

        // Simulates refresh/retry after the first response was lost: no second mutation is needed.
        using (var retry = await client.SendAsync(Request(
                   HttpMethod.Post,
                   $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
                   s.Owner,
                   body)))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            using var result = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
            Assert.True(result.RootElement.GetProperty("alreadyApplied").GetBoolean());
        }

        using var listResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={s.Space}",
            s.Owner));
        var rows = await listResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        var visible = Assert.Single(rows!);
        Assert.Equal(canonical, visible.GetProperty("id").GetGuid());

        using var activityResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts/{canonical}/activity?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.OK, activityResponse.StatusCode);
        using var activity = JsonDocument.Parse(await activityResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, activity.RootElement.GetProperty("matchedCount").GetInt32());
    }

    [Fact]
    public async Task MergeExecute_IdempotentRetryStillRequiresWriteAccess()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var viewer = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = viewer,
                EmailNormalized = $"{viewer:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Merge retry viewer",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = s.Space,
                UserId = viewer,
                Role = "member"
            });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = s.AccountA, UserId = viewer, OwnershipType = AccountOwnershipTypes.Viewer },
                new AccountOwner { AccountId = s.AccountB, UserId = viewer, OwnershipType = AccountOwnershipTypes.Viewer });
            await db.SaveChangesAsync();
        });

        using var previewResponse = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source } }));
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var body = new
        {
            contractIds = new[] { s.Target, s.Source },
            canonicalContractId = previewJson.RootElement.GetProperty("canonicalContractId").GetGuid(),
            previewToken = previewJson.RootElement.GetProperty("previewToken").GetString()
        };

        using var ownerExecute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            body));
        Assert.Equal(HttpStatusCode.OK, ownerExecute.StatusCode);

        using var viewerRetry = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
            viewer,
            body));

        Assert.Equal(HttpStatusCode.Forbidden, viewerRetry.StatusCode);
    }

    [Fact]
    public async Task MergeExecute_RejectsChangedStateAfterPreview()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var previewResponse = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source } }));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var canonical = previewJson.RootElement.GetProperty("canonicalContractId").GetGuid();
        var token = previewJson.RootElement.GetProperty("previewToken").GetString();

        await factory.SeedAsync(async db =>
        {
            var contract = await db.Contracts.SingleAsync(row => row.Id == canonical);
            contract.Name += " changed";
            contract.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync();
        });

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new
            {
                contractIds = new[] { s.Target, s.Source },
                canonicalContractId = canonical,
                previewToken = token
            }));

        Assert.Equal(HttpStatusCode.Conflict, execute.StatusCode);

        using var listResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={s.Space}",
            s.Owner));
        var rows = await listResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.Equal(2, rows!.Count);
    }

    [Fact]
    public async Task MergeExecute_IsForbiddenForReadOnlyMember()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var viewer = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = viewer,
                EmailNormalized = $"{viewer:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Merge viewer",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = s.Space,
                UserId = viewer,
                Role = "member"
            });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = s.AccountA, UserId = viewer, OwnershipType = AccountOwnershipTypes.Viewer },
                new AccountOwner { AccountId = s.AccountB, UserId = viewer, OwnershipType = AccountOwnershipTypes.Viewer });
            await db.SaveChangesAsync();
        });

        using var previewResponse = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            viewer,
            new { contractIds = new[] { s.Target, s.Source } }));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        Assert.False(previewJson.RootElement.GetProperty("executionEnabled").GetBoolean());

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
            viewer,
            new
            {
                contractIds = new[] { s.Target, s.Source },
                canonicalContractId = previewJson.RootElement.GetProperty("canonicalContractId").GetGuid(),
                previewToken = previewJson.RootElement.GetProperty("previewToken").GetString()
            }));

        Assert.Equal(HttpStatusCode.Forbidden, execute.StatusCode);
    }

    [Fact]
    public async Task MergeExecute_ConflictsWhenSourceWasMergedElsewhereAfterPreview()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var previewResponse = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source } }));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        var canonical = previewJson.RootElement.GetProperty("canonicalContractId").GetGuid();
        var source = canonical == s.Target ? s.Source : s.Target;
        var elsewhere = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Contracts.Add(new RecurringContract
            {
                Id = elsewhere,
                FullWorthSpaceId = s.Space,
                Name = "Other canonical",
                Amount = 182m,
                Currency = "EUR",
                BillingCycle = "monthly",
                IsActive = true
            });
            var sourceRow = await db.Contracts.SingleAsync(row => row.Id == source);
            sourceRow.MergedIntoContractId = elsewhere;
            sourceRow.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await db.SaveChangesAsync();
        });

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new
            {
                contractIds = new[] { s.Target, s.Source },
                canonicalContractId = canonical,
                previewToken = previewJson.RootElement.GetProperty("previewToken").GetString()
            }));

        Assert.Equal(HttpStatusCode.Conflict, execute.StatusCode);
    }

    [Fact]
    public async Task MergeExecutionKillSwitchHidesActionAndRejectsExecute()
    {
        using var factory = new BackendWebApplicationFactory(new Dictionary<string, string?>
        {
            [$"{FullWorth.Backend.Modules.Intelligence.AutopilotRolloutSettings.SectionName}:{FullWorth.Backend.Modules.Intelligence.AutopilotFeatures.ContractMergeExecution}"] = "off"
        });
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var previewResponse = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source } }));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        Assert.False(previewJson.RootElement.GetProperty("executionEnabled").GetBoolean());

        using var execute = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-execute?fullWorthSpaceId={s.Space}",
            s.Owner,
            new
            {
                contractIds = new[] { s.Target, s.Source },
                canonicalContractId = previewJson.RootElement.GetProperty("canonicalContractId").GetGuid(),
                previewToken = previewJson.RootElement.GetProperty("previewToken").GetString()
            }));

        Assert.Equal(HttpStatusCode.Forbidden, execute.StatusCode);
    }

    [Fact]
    public async Task MergePreview_SelectsLatestPaymentAsCanonical_AndDoesNotMutateContracts()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source } }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var preview = json.RootElement;

        Assert.Equal(s.Source, preview.GetProperty("canonicalContractId").GetGuid());
        Assert.True(preview.GetProperty("executionEnabled").GetBoolean());
        Assert.Equal(2, preview.GetProperty("combinedPaymentCount").GetInt32());
        Assert.Equal(2, preview.GetProperty("accountIds").GetArrayLength());
        Assert.Equal(2, preview.GetProperty("contracts").GetArrayLength());
        Assert.Contains(
            preview.GetProperty("canonicalFields").EnumerateArray(),
            field => field.GetProperty("field").GetString() == "accountId" &&
                     field.GetProperty("sourceContractId").GetGuid() == s.Source);

        using var listResponse = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var contracts = await listResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.Equal(2, contracts!.Count);

        using var targetSources = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts/{s.Target}/merged-sources?fullWorthSpaceId={s.Space}",
            s.Owner));
        using var sourceSources = await client.SendAsync(Request(
            HttpMethod.Get,
            $"/api/contracts/{s.Source}/merged-sources?fullWorthSpaceId={s.Space}",
            s.Owner));
        Assert.Equal(HttpStatusCode.OK, targetSources.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sourceSources.StatusCode);
        Assert.Empty((await targetSources.Content.ReadFromJsonAsync<List<JsonElement>>())!);
        Assert.Empty((await sourceSources.Content.ReadFromJsonAsync<List<JsonElement>>())!);
    }

    [Fact]
    public async Task MergePreview_RejectsMixedCurrencies()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await factory.SeedAsync(async db =>
        {
            var source = await db.Contracts.SingleAsync(contract => contract.Id == s.Source);
            source.Currency = "USD";
            await db.SaveChangesAsync();
        });

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MergePreview_RejectsSingleContract()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contracts/merge-preview?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Merge_HidesDuplicateButKeepsAccountAndPaymentHistory_AndCanBeUndone()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var merge = await client.SendAsync(Request(
            HttpMethod.Post,
            $"/api/contract-parity/merge?fullWorthSpaceId={s.Space}",
            s.Owner,
            new { contractIds = new[] { s.Target, s.Source }, targetContractId = s.Target }));
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
            $"/api/contract-parity/merge/{s.Target}/{s.Source}?fullWorthSpaceId={s.Space}",
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

    [Fact]
    public void MergePreviewService_RemainsReadOnly()
    {
        var path = Path.Combine(
            Root(),
            "src",
            "FullWorth.Backend",
            "Modules",
            "Contracts",
            "ContractMergePreviewService.cs");
        var content = File.ReadAllText(path);

        Assert.DoesNotContain("SaveChanges", content, StringComparison.Ordinal);
        Assert.DoesNotContain("MergeForUserAsync", content, StringComparison.Ordinal);
        Assert.DoesNotContain("MergedIntoContractId =", content, StringComparison.Ordinal);
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

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
