using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Api;

public sealed class ImportMappingRegressionTests
{
    [Fact]
    public async Task DetectUnderstandsGermanSemicolonCsvHeaders()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "Datum;Betrag;Währung;Empfänger;Konto\r\n30.08.2026;-12,34;EUR;REWE;Giro\r\n";
        using var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/detect?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = FileOnly(csv);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, doc.RootElement.GetProperty("rowCount").GetInt32());
        var mapping = doc.RootElement.GetProperty("suggestedMapping");
        Assert.Equal("Datum", mapping.GetProperty("date").GetString());
        Assert.Equal("Betrag", mapping.GetProperty("amount").GetString());
        Assert.Equal("Währung", mapping.GetProperty("currency").GetString());
        Assert.Equal("Empfänger", mapping.GetProperty("counterparty").GetString());
        Assert.Equal("Konto", mapping.GetProperty("account").GetString());
    }

    [Fact]
    public async Task UploadKeepsValidRowsAndMarksMalformedRowsForReview()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "Datum;Betrag;Empfänger\r\n30.08.2026;-12,34;REWE\r\nnot-a-date;5,00;Broken\r\n";
        using var response = await Upload(client, owner, csv, new
        {
            date = "Datum",
            amount = "Betrag",
            currency = (string?)null,
            counterparty = "Empfänger",
            description = (string?)null,
            account = (string?)null,
            category = (string?)null,
            externalKey = (string?)null
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("sourceRows").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("ready").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("errors").GetInt32());
    }

    [Fact]
    public async Task CommitRejectsAccountThatCallerCannotWrite()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var hiddenAccount = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await SeedAccount(factory, hiddenAccount, null);

        using var upload = await Upload(client, owner,
            "Datum;Betrag;Empfänger\r\n30.08.2026;-12,34;REWE\r\n",
            BasicMapping());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");

        using var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            sourceAccountMappings = new Dictionary<string, Guid?>(),
            defaultAccountId = hiddenAccount,
            categoryMappings = new Dictionary<string, Guid?>(),
            createMissingCategories = false,
            runFullWorthCategorization = false,
            candidateIds = (Guid[]?)null
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReimportOfSameRowsIsDetectedSemanticallyAcrossJobs()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await SeedAccount(factory, account, owner);
        const string csv = "Datum;Betrag;Empfänger;Text\r\n29.08.2026;-12,34;REWE;Lebensmittel\r\n30.08.2026;-5,00;Bäckerei;Frühstück\r\n";

        var first = await UploadAndCommit(client, owner, account, csv);
        Assert.Equal(2, first.GetProperty("imported").GetInt32());
        Assert.Equal(0, first.GetProperty("duplicates").GetInt32());

        var second = await UploadAndCommit(client, owner, account, csv);
        Assert.Equal(0, second.GetProperty("imported").GetInt32());
        Assert.Equal(2, second.GetProperty("duplicates").GetInt32());
    }

    private static async Task<JsonElement> UploadAndCommit(HttpClient client, Guid owner, Guid account, string csv)
    {
        using var upload = await Upload(client, owner, csv, new
        {
            date = "Datum",
            amount = "Betrag",
            currency = (string?)null,
            counterparty = "Empfänger",
            description = "Text",
            account = (string?)null,
            category = (string?)null,
            externalKey = (string?)null
        });
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
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, Guid user, string csv, object mapping)
    {
        var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "transactions.csv");
        content.Add(new StringContent(JsonSerializer.Serialize(mapping), Encoding.UTF8, "application/json"), "mapping");
        request.Content = content;
        return await client.SendAsync(request);
    }

    private static MultipartFormDataContent FileOnly(string csv)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "transactions.csv");
        return content;
    }

    private static object BasicMapping() => new
    {
        date = "Datum",
        amount = "Betrag",
        currency = (string?)null,
        counterparty = "Empfänger",
        description = (string?)null,
        account = (string?)null,
        category = (string?)null,
        externalKey = (string?)null
    };

    private static Guid ReadGuid(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(property).GetGuid();
    }

    private static async Task SeedOwner(BackendWebApplicationFactory factory, Guid userId)
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
            await db.SaveChangesAsync();
        });
    }

    private static async Task SeedAccount(BackendWebApplicationFactory factory, Guid accountId, Guid? owner)
    {
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"import-{accountId:N}",
                ProviderAccountId = $"import-{accountId:N}",
                InstitutionName = "Import Test",
                DisplayName = "Import account",
                Currency = "EUR",
                IsActive = true
            });
            if (owner.HasValue)
            {
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = accountId,
                    UserId = owner.Value,
                    OwnershipType = AccountOwnershipTypes.Owner
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
