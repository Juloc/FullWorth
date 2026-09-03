using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class AdvancedTransactionBulkRegressionTests
{
    [Fact]
    public async Task FilterApplyRejectsStalePreviewWithoutChangingTransactions()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwnerAndAccounts(factory, owner, account);
        await SeedTransaction(factory, account, "bulk-coffee-1", -10m, "BULK COFFEE");
        await SeedTransaction(factory, account, "bulk-coffee-2", -20m, "BULK COFFEE");

        var filter = new { query = "BULK COFFEE", includeIgnored = false };
        using var previewRequest = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/advanced-preview?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        previewRequest.Content = JsonContent.Create(new
        {
            filter,
            transactionIds = (Guid[]?)null,
            expectedCount = 0,
            confirmSelection = false
        });
        using var previewResponse = await client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, preview.RootElement.GetProperty("count").GetInt32());

        await SeedTransaction(factory, account, "bulk-coffee-3", -30m, "BULK COFFEE");

        using var applyRequest = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/apply?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        applyRequest.Content = JsonContent.Create(new
        {
            filter,
            transactionIds = (Guid[]?)null,
            expectedCount = 2,
            confirmSelection = true,
            isIgnored = true
        });
        using var applyResponse = await client.SendAsync(applyRequest);
        Assert.Equal(HttpStatusCode.Conflict, applyResponse.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.All(await db.Transactions.AsNoTracking().Where(tx => tx.Counterparty == "BULK COFFEE").ToListAsync(), tx => Assert.False(tx.IsIgnored)));
    }

    [Fact]
    public async Task ReplaceNotesRequiresSecondExplicitConfirmation()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwnerAndAccounts(factory, owner, account);
        var transactionId = await SeedTransaction(factory, account, "bulk-note", -12m, "NOTE SHOP", "original note");

        using var denied = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/apply?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        denied.Content = JsonContent.Create(new
        {
            filter = (object?)null,
            transactionIds = new[] { transactionId },
            expectedCount = 1,
            confirmSelection = true,
            replaceNotes = true,
            note = "replacement",
            confirmReplaceNotes = false
        });
        using var deniedResponse = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.BadRequest, deniedResponse.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Equal("original note", (await db.Transactions.AsNoTracking().SingleAsync(tx => tx.Id == transactionId)).UserNote));

        using var accepted = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/apply?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        accepted.Content = JsonContent.Create(new
        {
            filter = (object?)null,
            transactionIds = new[] { transactionId },
            expectedCount = 1,
            confirmSelection = true,
            replaceNotes = true,
            note = "replacement",
            confirmReplaceNotes = true
        });
        using var acceptedResponse = await client.SendAsync(accepted);
        Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Equal("replacement", (await db.Transactions.AsNoTracking().SingleAsync(tx => tx.Id == transactionId)).UserNote));
    }

    [Fact]
    public async Task SafeTwoLegSelectionCanBePairedAsTransfer()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        await SeedOwnerAndAccounts(factory, owner, accountA, accountB);
        var debit = await SeedTransaction(factory, accountA, "transfer-out", -125m, "OWN TRANSFER");
        var credit = await SeedTransaction(factory, accountB, "transfer-in", 125m, "OWN TRANSFER");

        using var previewRequest = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/advanced-preview?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        previewRequest.Content = JsonContent.Create(new
        {
            filter = (object?)null,
            transactionIds = new[] { debit, credit },
            expectedCount = 0,
            confirmSelection = false
        });
        using var previewResponse = await client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using (var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync()))
            Assert.True(preview.RootElement.GetProperty("canPairTransfer").GetBoolean());

        using var applyRequest = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/apply?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        applyRequest.Content = JsonContent.Create(new
        {
            filter = (object?)null,
            transactionIds = new[] { debit, credit },
            expectedCount = 2,
            confirmSelection = true,
            pairAsTransfer = true
        });
        using var applyResponse = await client.SendAsync(applyRequest);
        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var rows = await db.Transactions.AsNoTracking().Where(tx => tx.Id == debit || tx.Id == credit).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, tx => Assert.True(tx.IsTransfer));
            Assert.All(rows, tx => Assert.True(tx.TransferGroupId.HasValue));
            Assert.Single(rows.Select(tx => tx.TransferGroupId).Distinct());
        });
    }

    [Fact]
    public async Task ContractBulkLinkRejectsExistingAllocationAndLinksCleanExpensesByFullAmount()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwnerAndAccounts(factory, owner, account);
        var existingTx = await SeedTransaction(factory, account, "contract-existing", -15m, "SUBSCRIPTION");
        var cleanTx = await SeedTransaction(factory, account, "contract-clean", -25m, "SUBSCRIPTION");
        var contractId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Contracts.Add(new RecurringContract
            {
                Id = contractId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Bulk Contract",
                AccountId = account,
                Amount = 25m,
                Currency = "EUR",
                BillingCycle = "monthly",
                IsActive = true
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ContractTransactionLinks"
("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","CreatedAt")
VALUES ({Guid.NewGuid()},{FullWorthSpaceDefaults.LegacyId},{contractId},{existingTx},{15m},{"manual"},{DateTimeOffset.UtcNow})
""");
        });

        using var conflict = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/apply?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        conflict.Content = JsonContent.Create(new
        {
            filter = (object?)null,
            transactionIds = new[] { existingTx, cleanTx },
            expectedCount = 2,
            confirmSelection = true,
            contractAction = "link",
            contractId
        });
        using var conflictResponse = await client.SendAsync(conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

        using var success = UserRequest(HttpMethod.Post,
            $"/api/transaction-bulk/apply?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        success.Content = JsonContent.Create(new
        {
            filter = (object?)null,
            transactionIds = new[] { cleanTx },
            expectedCount = 1,
            confirmSelection = true,
            contractAction = "link",
            contractId
        });
        using var successResponse = await client.SendAsync(success);
        Assert.Equal(HttpStatusCode.OK, successResponse.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Amount\" FROM \"ContractTransactionLinks\" WHERE \"ContractId\"=@contract AND \"TransactionId\"=@tx";
            var contractParameter = command.CreateParameter(); contractParameter.ParameterName = "@contract"; contractParameter.Value = contractId; command.Parameters.Add(contractParameter);
            var txParameter = command.CreateParameter(); txParameter.ParameterName = "@tx"; txParameter.Value = cleanTx; command.Parameters.Add(txParameter);
            Assert.Equal(25m, Convert.ToDecimal(await command.ExecuteScalarAsync()));
        });
    }

    private static async Task SeedOwnerAndAccounts(BackendWebApplicationFactory factory, Guid owner, params Guid[] accountIds)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Advanced bulk owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            foreach (var accountId in accountIds)
            {
                db.Accounts.Add(new FinanceAccount
                {
                    Id = accountId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    BankConnectionId = null,
                    Provider = "manual",
                    ProviderAccountId = $"manual-{accountId:N}",
                    IdentificationHash = $"manual-{accountId:N}",
                    InstitutionName = "Manual",
                    DisplayName = $"Account {accountId:N}",
                    Currency = "EUR"
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = accountId,
                    UserId = owner,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
            }
            await db.SaveChangesAsync();
        });
    }

    private static async Task<Guid> SeedTransaction(
        BackendWebApplicationFactory factory,
        Guid accountId,
        string externalKey,
        decimal amount,
        string counterparty,
        string? note = null)
    {
        var id = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction
            {
                Id = id,
                AccountId = accountId,
                ExternalKey = externalKey,
                BookingDate = new DateOnly(2026, 8, 30),
                Amount = amount,
                Currency = "EUR",
                Counterparty = counterparty,
                UserNote = note
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
