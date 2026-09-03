using System.Data;
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

public sealed class ImportAtomicityRegressionTests
{
    [Fact]
    public async Task CommitFailureRollsBackTransactionsCandidateMarkersAndJobCompletion()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwnerAndAccount(factory, owner, account);

        const string csv =
            "Datum;Betrag;Empfänger;ID\r\n" +
            "29.08.2026;-10,00;REWE;row-1\r\n" +
            "30.08.2026;-20,00;LIDL;row-2\r\n";

        using var upload = await Upload(client, owner, csv);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var uploadJson = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var jobId = uploadJson.RootElement.GetProperty("jobId").GetGuid();

        // Force a real database failure during the final transaction insert. Candidate duplicate-state
        // updates happen before SaveChanges, so this proves the raw SQL staging writes and EF inserts are
        // participating in the same transaction and roll back together.
        await factory.SeedAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("""
CREATE OR REPLACE FUNCTION test_import_atomicity_failure() RETURNS trigger AS $$
BEGIN
  IF NEW."Counterparty" = 'LIDL' THEN
    RAISE EXCEPTION 'forced import atomicity failure';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS test_import_atomicity_failure ON "Transactions";
CREATE TRIGGER test_import_atomicity_failure
BEFORE INSERT ON "Transactions"
FOR EACH ROW EXECUTE FUNCTION test_import_atomicity_failure();
""");
        });

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
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.False(await db.Transactions.AsNoTracking().AnyAsync(x =>
                x.AccountId == account && (x.Counterparty == "REWE" || x.Counterparty == "LIDL")));

            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();

            await using (var job = connection.CreateCommand())
            {
                job.CommandText = "SELECT \"Status\",\"ImportedCount\",\"DuplicateCount\",\"CompletedAt\" FROM \"ImportJobs\" WHERE \"Id\"=@id";
                var p = job.CreateParameter(); p.ParameterName = "@id"; p.Value = jobId; job.Parameters.Add(p);
                await using var reader = await job.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("mapping_required", reader.GetString(0));
                Assert.Equal(0, reader.GetInt32(1));
                Assert.Equal(0, reader.GetInt32(2));
                Assert.True(reader.IsDBNull(3));
            }

            await using (var candidates = connection.CreateCommand())
            {
                candidates.CommandText = "SELECT \"DuplicateStatus\" FROM \"ImportCandidates\" WHERE \"ImportJobId\"=@id ORDER BY \"Id\"";
                var p = candidates.CreateParameter(); p.ParameterName = "@id"; p.Value = jobId; candidates.Parameters.Add(p);
                await using var reader = await candidates.ExecuteReaderAsync();
                var states = new List<string>();
                while (await reader.ReadAsync()) states.Add(reader.GetString(0));
                Assert.Equal(2, states.Count);
                Assert.All(states, state => Assert.Equal("new", state));
            }
        });
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid user, string csv)
    {
        var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "atomicity.csv");
        content.Add(new StringContent(JsonSerializer.Serialize(new
        {
            date = "Datum",
            amount = "Betrag",
            currency = (string?)null,
            counterparty = "Empfänger",
            description = (string?)null,
            account = (string?)null,
            category = (string?)null,
            externalKey = "ID"
        }), Encoding.UTF8, "application/json"), "mapping");
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static async Task SeedOwnerAndAccount(BackendWebApplicationFactory factory, Guid owner, Guid account)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Atomic import owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"atomic-{account:N}",
                ProviderAccountId = $"atomic-{account:N}",
                InstitutionName = "Manual",
                DisplayName = "Atomic account",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
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
