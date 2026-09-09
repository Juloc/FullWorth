using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// A wrong transaction file used to be permanent: the commit left no record of which transactions it
/// created, so there was nothing to undo. Depot imports had a rollback; this is the transaction one.
/// </summary>
public sealed class ImportRollbackRegressionTests
{
    [Fact]
    public async Task RollbackRemovesTheImportedTransactionsAndCanOnlyRunOnce()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await Seed(factory, owner, account);

        var jobId = await UploadAndCommit(client, owner, account,
            "Datum;Betrag;Empfänger\r\n29.08.2026;-12,34;REWE\r\n30.08.2026;-5,00;Bäckerei\r\n");
        Assert.Equal(2, await CountTransactions(factory, account));

        var rollback = await Rollback(client, owner, jobId);
        Assert.Equal(HttpStatusCode.OK, rollback.Status);
        Assert.Equal(2, rollback.Body!.Value.GetProperty("removed").GetInt32());
        Assert.Equal(0, rollback.Body!.Value.GetProperty("kept").GetInt32());
        Assert.Equal(0, await CountTransactions(factory, account));

        var second = await Rollback(client, owner, jobId);
        Assert.Equal(HttpStatusCode.BadRequest, second.Status);
    }

    [Fact]
    public async Task RollbackKeepsATransactionTheUserHasAlreadyWorkedOn()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await Seed(factory, owner, account);

        var jobId = await UploadAndCommit(client, owner, account,
            "Datum;Betrag;Empfänger\r\n29.08.2026;-12,34;REWE\r\n30.08.2026;-5,00;Bäckerei\r\n");

        // Marking a booking as reviewed is user work. Its foreign key cascades, so an unguarded
        // rollback would delete that decision along with the transaction.
        await factory.SeedAsync(async db =>
        {
            var transactionId = await db.Transactions.AsNoTracking()
                .Where(t => t.AccountId == account).OrderBy(t => t.BookingDate)
                .Select(t => t.Id).FirstAsync();
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO \"TransactionReviewStates\" (\"TransactionId\",\"FullWorthSpaceId\",\"IsReviewed\",\"UpdatedAt\")"
                + " VALUES (@transaction,@space,true,now())";
            var transactionParameter = command.CreateParameter();
            transactionParameter.ParameterName = "@transaction";
            transactionParameter.Value = transactionId;
            command.Parameters.Add(transactionParameter);
            var spaceParameter = command.CreateParameter();
            spaceParameter.ParameterName = "@space";
            spaceParameter.Value = FullWorthSpaceDefaults.LegacyId;
            command.Parameters.Add(spaceParameter);
            await command.ExecuteNonQueryAsync();
        });

        var rollback = await Rollback(client, owner, jobId);
        Assert.Equal(HttpStatusCode.OK, rollback.Status);
        Assert.Equal(1, rollback.Body!.Value.GetProperty("removed").GetInt32());
        Assert.Equal(1, rollback.Body!.Value.GetProperty("kept").GetInt32());
        Assert.Equal(1, await CountTransactions(factory, account));
    }

    [Fact]
    public async Task AnImportThatWasNeverCommittedCannotBeRolledBack()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await Seed(factory, owner, account);

        using var upload = await Upload(client, owner, "Datum;Betrag;Empfänger\r\n29.08.2026;-12,34;REWE\r\n");
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");

        var rollback = await Rollback(client, owner, jobId);
        Assert.Equal(HttpStatusCode.BadRequest, rollback.Status);
    }

    private static async Task<(HttpStatusCode Status, JsonElement? Body)> Rollback(HttpClient client, Guid owner, Guid jobId)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/import-jobs/{jobId:D}/rollback?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, doc.RootElement.Clone());
    }

    private static async Task<int> CountTransactions(BackendWebApplicationFactory factory, Guid account)
    {
        var count = 0;
        await factory.SeedAsync(async db =>
            count = await db.Transactions.AsNoTracking().CountAsync(t => t.AccountId == account));
        return count;
    }

    private static async Task<Guid> UploadAndCommit(HttpClient client, Guid owner, Guid account, string csv)
    {
        using var upload = await Upload(client, owner, csv);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");

        using var commit = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        commit.Content = JsonContent.Create(new
        {
            sourceAccountMappings = new Dictionary<string, Guid?>(),
            defaultAccountId = account,
            categoryMappings = new Dictionary<string, Guid?>(),
            createMissingCategories = false,
            runFullWorthCategorization = false,
            candidateIds = (Guid[]?)null
        });
        using var response = await client.SendAsync(commit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return jobId;
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid user, string csv)
    {
        var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "transactions.csv");
        content.Add(new StringContent(JsonSerializer.Serialize(new
        {
            date = "Datum",
            amount = "Betrag",
            currency = (string?)null,
            counterparty = "Empfänger",
            description = (string?)null,
            account = (string?)null,
            category = (string?)null,
            externalKey = (string?)null
        }), Encoding.UTF8, "application/json"), "mapping");
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string url, Guid userId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static Guid ReadGuid(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(property).GetGuid();
    }

    private static async Task Seed(BackendWebApplicationFactory factory, Guid userId, Guid accountId)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Import owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"rollback-{accountId:N}",
                ProviderAccountId = $"rollback-{accountId:N}",
                InstitutionName = "Rollback Test",
                DisplayName = "Giro",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });
    }
}
