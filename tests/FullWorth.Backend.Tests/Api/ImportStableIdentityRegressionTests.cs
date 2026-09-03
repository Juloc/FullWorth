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

public sealed class ImportStableIdentityRegressionTests
{
    [Fact]
    public async Task SameExternalIdInsideOneFileImportsOnlyOnce()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwnerAndAccount(factory, owner, account);

        const string csv =
            "Datum;Betrag;Empfänger;ID\r\n" +
            "29.08.2026;-10,00;REWE;bank-42\r\n" +
            "30.08.2026;-20,00;LIDL;bank-42\r\n";

        var result = await UploadAndCommit(client, owner, account, csv);
        Assert.Equal(1, result.GetProperty("imported").GetInt32());
        Assert.Equal(1, result.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task StableExternalIdWinsEvenWhenSemanticFieldsChangeOnReimport()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwnerAndAccount(factory, owner, account);

        const string firstCsv =
            "Datum;Betrag;Empfänger;ID\r\n" +
            "29.08.2026;-10,00;REWE;bank-99\r\n";
        const string changedCsv =
            "Datum;Betrag;Empfänger;ID\r\n" +
            "30.08.2026;-12,50;REWE MARKT;bank-99\r\n";

        var first = await UploadAndCommit(client, owner, account, firstCsv);
        Assert.Equal(1, first.GetProperty("imported").GetInt32());
        Assert.Equal(0, first.GetProperty("duplicates").GetInt32());

        var second = await UploadAndCommit(client, owner, account, changedCsv);
        Assert.Equal(0, second.GetProperty("imported").GetInt32());
        Assert.Equal(1, second.GetProperty("duplicates").GetInt32());
    }

    private static async Task<JsonElement> UploadAndCommit(HttpClient client, Guid user, Guid account, string csv)
    {
        using var upload = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        multipart.Add(file, "file", "stable-id.csv");
        multipart.Add(new StringContent(JsonSerializer.Serialize(new
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
        upload.Content = multipart;
        using var uploadResponse = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        using var uploadJson = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync());
        var jobId = uploadJson.RootElement.GetProperty("jobId").GetGuid();

        using var commit = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
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
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return result.RootElement.Clone();
    }

    private static async Task SeedOwnerAndAccount(BackendWebApplicationFactory factory, Guid owner, Guid account)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Stable import owner",
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
                IdentificationHash = $"stable-{account:N}",
                ProviderAccountId = $"stable-{account:N}",
                InstitutionName = "Manual",
                DisplayName = "Stable import account",
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
