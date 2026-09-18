using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Finanzguru already writes into the same "ImportJobs"/"ImportTransactionLinks" machinery as every
/// other file import (#131), so the generic rollback endpoint was already mechanically reachable for
/// it. Three things still made a real Finanzguru rollback fail or overreach (#175):
///
/// 1. A run that creates more than one account (one per source account in the file) had nowhere to
///    record the second and third one - "ImportJobs.CreatedAccountId" is a single column.
/// 2. Its own split rows (Teilbuchung/Restbetrag) wrote "TransactionAllocations" as part of the same
///    import, and the rollback guard treated that exactly like a user's manual split - blocking the
///    import's own rollback.
/// 3. Linking a historical account onto a real one ("moved", not merged) left the old
///    "ImportTransactionLinks" row in place, so a later rollback of the original import could still
///    delete a transaction now living on an actively used bank account.
/// </summary>
public sealed class FinanzguruRollbackTests
{
    [Fact]
    public async Task RollbackRemovesEveryAccountTheImportCreated()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedSpaceAsync(factory);

        FinanzguruImportResult? result;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinanzguruImportService>();
            result = await service.ImportRowsAsync(userId, spaceId, [
                Row("acct-a-1", new DateOnly(2026, 8, 3), -10m, "Kaufland", "DE1111000000000000A1", "Konto A"),
                Row("acct-b-1", new DateOnly(2026, 8, 4), -20m, "Rewe", "DE2222000000000000B2", "Konto B")
            ], CancellationToken.None);
        }
        Assert.NotNull(result);
        Assert.Equal(2, result!.AccountsCreated);

        var jobId = await SingleJobIdAsync(factory, spaceId);
        Assert.Equal(2, await ImportedAccountCountAsync(factory, spaceId));

        var rollback = await Rollback(client, userId, spaceId, jobId);
        Assert.Equal(HttpStatusCode.OK, rollback.Status);
        Assert.Equal(2, rollback.Body!.Value.GetProperty("removed").GetInt32());

        Assert.Equal(0, await ImportedAccountCountAsync(factory, spaceId));
    }

    [Fact]
    public async Task RollbackRemovesASplitTransactionAndItsOwnJobAllocations()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedSpaceAsync(factory);

        const string referenceAccount = "DE3333000000000000C3";
        FinanzguruImportResult? result;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinanzguruImportService>();
            result = await service.ImportRowsAsync(userId, spaceId, [
                Row("fg-orig-1", new DateOnly(2026, 8, 5), -50m, "Elektronikmarkt", referenceAccount, "Konto C", splitType: "Original"),
                Row("fg-child-1", new DateOnly(2026, 8, 5), -30m, "Elektronikmarkt", referenceAccount, "Konto C", splitType: "Teilbuchung", originalReferenceId: "fg-orig-1"),
                Row("fg-child-2", new DateOnly(2026, 8, 5), -20m, "Elektronikmarkt", referenceAccount, "Konto C", splitType: "Restbetrag", originalReferenceId: "fg-orig-1")
            ], CancellationToken.None);
        }
        Assert.NotNull(result);
        Assert.Equal(1, result!.SplitTransactions);
        Assert.Equal(1, result.TransactionsImported);

        var jobId = await SingleJobIdAsync(factory, spaceId);
        var transactionId = await TransactionIdByExternalKeyAsync(factory, spaceId, "finanzguru:fg-orig-1");

        await factory.SeedAsync(async db =>
        {
            var allocations = await db.TransactionAllocations.AsNoTracking()
                .Where(allocation => allocation.TransactionId == transactionId)
                .ToListAsync();
            Assert.Equal(2, allocations.Count);
            Assert.All(allocations, allocation => Assert.Equal(jobId, allocation.CreatedByImportJobId));
        });

        var rollback = await Rollback(client, userId, spaceId, jobId);
        Assert.Equal(HttpStatusCode.OK, rollback.Status);
        Assert.Equal(1, rollback.Body!.Value.GetProperty("removed").GetInt32());

        await factory.SeedAsync(async db =>
        {
            Assert.False(await db.Transactions.AsNoTracking().AnyAsync(transaction => transaction.Id == transactionId));
            Assert.False(await db.TransactionAllocations.AsNoTracking().AnyAsync(allocation => allocation.TransactionId == transactionId));
        });
    }

    [Fact]
    public async Task AManuallyAddedSplitStillBlocksRollbackForThatTransaction()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedSpaceAsync(factory);

        const string referenceAccount = "DE4444000000000000D4";
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinanzguruImportService>();
            var result = await service.ImportRowsAsync(userId, spaceId, [
                Row("fg-plain-1", new DateOnly(2026, 8, 6), -12m, "Baecker", referenceAccount, "Konto D"),
                Row("fg-plain-2", new DateOnly(2026, 8, 7), -34m, "Drogerie", referenceAccount, "Konto D")
            ], CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(2, result!.TransactionsImported);
        }

        var jobId = await SingleJobIdAsync(factory, spaceId);
        var editedTransactionId = await TransactionIdByExternalKeyAsync(factory, spaceId, "finanzguru:fg-plain-1");
        var otherTransactionId = await TransactionIdByExternalKeyAsync(factory, spaceId, "finanzguru:fg-plain-2");

        // A split the USER added after the import, on a transaction that import created. Its
        // CreatedByImportJobId is NULL - the safe default for every writer except the import that
        // stamps its own splits - and NULL must keep blocking the rollback exactly as before.
        await factory.SeedAsync(async db =>
        {
            db.TransactionAllocations.Add(new TransactionAllocation
            {
                TransactionId = editedTransactionId,
                Amount = -12m,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var rollback = await Rollback(client, userId, spaceId, jobId);
        Assert.Equal(HttpStatusCode.OK, rollback.Status);
        Assert.Equal(1, rollback.Body!.Value.GetProperty("removed").GetInt32());
        Assert.Equal(1, rollback.Body!.Value.GetProperty("kept").GetInt32());

        await factory.SeedAsync(async db =>
        {
            Assert.True(await db.Transactions.AsNoTracking().AnyAsync(transaction => transaction.Id == editedTransactionId));
            Assert.False(await db.Transactions.AsNoTracking().AnyAsync(transaction => transaction.Id == otherTransactionId));
        });
    }

    [Fact]
    public async Task LinkingAMovedHistoricalTransactionClearsItsProvenanceLinkImmediately()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (userId, spaceId) = await SeedSpaceAsync(factory);

        // Two source accounts in the same file, so one can be linked away (moved) while the other stays
        // untouched on its own import account - a single-account import has no transaction left to
        // still be linked afterwards, which would make the rollback itself unavailable rather than
        // demonstrating that it leaves the moved row alone.
        const string movedReferenceAccount = "DE5555000000000000E5";
        const string stayingReferenceAccount = "DE6666000000000000F6";
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinanzguruImportService>();
            var result = await service.ImportRowsAsync(userId, spaceId, [
                Row("fg-move-1", new DateOnly(2021, 1, 5), -8m, "Kiosk", movedReferenceAccount, "Altes Konto"),
                Row("fg-stay-1", new DateOnly(2021, 1, 6), -9m, "Kino", stayingReferenceAccount, "Anderes Konto")
            ], CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(2, result!.TransactionsImported);
            Assert.Equal(2, result.AccountsCreated);
        }

        var jobId = await SingleJobIdAsync(factory, spaceId);
        var movedTransactionId = await TransactionIdByExternalKeyAsync(factory, spaceId, "finanzguru:fg-move-1");
        var movedImportAccountId = await ImportAccountIdByTransactionAsync(factory, movedTransactionId);
        Assert.Equal(2, await LinkCountAsync(factory, jobId));

        var liveAccountId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = liveAccountId,
                FullWorthSpaceId = spaceId,
                Provider = "enable-banking",
                IdentificationHash = $"live-{liveAccountId:N}",
                ProviderAccountId = $"live-{liveAccountId:N}",
                InstitutionName = "Bank",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = liveAccountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });

        // The row is historical, older than anything a live bank sync would carry, so it matches
        // nothing on the (empty) live account and the reconciliation moves it - it has nothing to merge.
        using (var scope = factory.Services.CreateScope())
        {
            var reconciliation = scope.ServiceProvider.GetRequiredService<FinanzguruAccountReconciliationService>();
            var linkResult = await reconciliation.LinkExplicitAsync(
                userId, spaceId, movedImportAccountId, liveAccountId, null, null, CancellationToken.None);
            Assert.NotNull(linkResult);
            Assert.Equal(1, linkResult!.TransactionsMoved);
        }

        // The fix: the moved row's own provenance link is gone right away, not just after some later
        // rollback attempt fails to find it.
        Assert.Equal(0, await LinkCountForTransactionAsync(factory, movedTransactionId));
        // The other row, still on its own import account, is untouched and keeps the job rollback-eligible.
        Assert.Equal(1, await LinkCountAsync(factory, jobId));

        var rollback = await Rollback(client, userId, spaceId, jobId);
        Assert.Equal(HttpStatusCode.OK, rollback.Status);
        Assert.Equal(1, rollback.Body!.Value.GetProperty("removed").GetInt32());

        await factory.SeedAsync(async db =>
        {
            // The moved booking now lives on a real, actively used bank account - the rollback of the
            // original Finanzguru import must not have touched it.
            var moved = await db.Transactions.AsNoTracking().SingleAsync(transaction => transaction.Id == movedTransactionId);
            Assert.Equal(liveAccountId, moved.AccountId);
            Assert.Equal(-8m, moved.Amount);

            // The row that stayed on its import account had nothing protecting it, so the rollback
            // removed it along with the now-empty import accounts.
            Assert.False(await db.Transactions.AsNoTracking().AnyAsync(transaction => transaction.ExternalKey == "finanzguru:fg-stay-1"));
        });
    }

    private static FinanzguruRow Row(
        string bookingId, DateOnly date, decimal amount, string counterparty,
        string referenceAccount, string referenceAccountName,
        string? splitType = null, string? originalReferenceId = null) =>
        new(
            RowNumber: 0,
            BookingDate: date,
            ReferenceAccount: referenceAccount,
            ReferenceAccountName: referenceAccountName,
            Amount: amount,
            Currency: "EUR",
            Counterparty: counterparty,
            CounterpartyIban: null,
            Description: counterparty,
            EntryReference: null,
            MainCategory: null,
            SubCategory: null,
            IsTransfer: false,
            BookingId: bookingId,
            OriginalReferenceId: originalReferenceId,
            SplitType: splitType,
            RawValues: new Dictionary<string, string?> { ["Betrag"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static async Task<(Guid UserId, Guid SpaceId)> SeedSpaceAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@LOCAL.TEST",
                DisplayName = "Test",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Test", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });
        return (userId, spaceId);
    }

    private static async Task<Guid> SingleJobIdAsync(BackendWebApplicationFactory factory, Guid spaceId)
    {
        var jobId = Guid.Empty;
        await factory.SeedAsync(async db =>
            jobId = await db.Database.SqlQuery<Guid>(
                    $"""SELECT "Id" AS "Value" FROM "ImportJobs" WHERE "FullWorthSpaceId" = {spaceId}""")
                .SingleAsync());
        return jobId;
    }

    // Each test's factory clones its own isolated database, so a plain ExternalKey match is unique
    // without also filtering by FullWorthSpaceId (FinanceTransaction has no navigation to its account).
    private static async Task<Guid> TransactionIdByExternalKeyAsync(BackendWebApplicationFactory factory, Guid spaceId, string externalKey)
    {
        var id = Guid.Empty;
        await factory.SeedAsync(async db =>
            id = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.ExternalKey == externalKey)
                .Select(transaction => transaction.Id)
                .SingleAsync());
        return id;
    }

    private static async Task<Guid> ImportAccountIdByTransactionAsync(BackendWebApplicationFactory factory, Guid transactionId)
    {
        var id = Guid.Empty;
        await factory.SeedAsync(async db =>
            id = await db.Transactions.AsNoTracking()
                .Where(transaction => transaction.Id == transactionId)
                .Select(transaction => transaction.AccountId)
                .SingleAsync());
        return id;
    }

    private static async Task<int> ImportedAccountCountAsync(BackendWebApplicationFactory factory, Guid spaceId)
    {
        var count = 0;
        await factory.SeedAsync(async db =>
            count = await db.Accounts.AsNoTracking()
                .CountAsync(account => account.FullWorthSpaceId == spaceId
                                        && account.Provider == FinanzguruAccountReconciliationService.ImportProvider));
        return count;
    }

    private static async Task<int> LinkCountAsync(BackendWebApplicationFactory factory, Guid jobId)
    {
        var count = 0;
        await factory.SeedAsync(async db =>
            count = await db.Database.SqlQuery<int>(
                    $"""SELECT count(*)::int AS "Value" FROM "ImportTransactionLinks" WHERE "ImportJobId" = {jobId}""")
                .SingleAsync());
        return count;
    }

    private static async Task<int> LinkCountForTransactionAsync(BackendWebApplicationFactory factory, Guid transactionId)
    {
        var count = 0;
        await factory.SeedAsync(async db =>
            count = await db.Database.SqlQuery<int>(
                    $"""SELECT count(*)::int AS "Value" FROM "ImportTransactionLinks" WHERE "TransactionId" = {transactionId}""")
                .SingleAsync());
        return count;
    }

    private static async Task<(HttpStatusCode Status, JsonElement? Body)> Rollback(HttpClient client, Guid userId, Guid spaceId, Guid jobId)
    {
        using var request = UserRequest(HttpMethod.Post, $"/api/import-jobs/{jobId:D}/rollback?fullWorthSpaceId={spaceId:D}", userId);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string url, Guid userId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
