using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentImportDuplicatePreviewTests
{
    private const string Isin = "DE000A1EWWW0";

    [Fact]
    public async Task PreviewFindsTradeAlreadyImportedIntoSelectedPortfolioAndCommitAgrees()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var portfolio = Guid.NewGuid();
        var security = Guid.NewGuid();
        await SeedOwnerPortfolioSecurity(factory, owner, portfolio, security);

        const string csv = "Datum;Typ;ISIN;Stück;Kurs;Betrag;Währung;ID\r\n" +
                           "30.08.2026;Kauf;DE000A1EWWW0;2;100,00;200,00;EUR;preview-existing-1\r\n";

        var firstJob = await Upload(client, owner, csv);
        using (var firstCommit = await Commit(client, owner, firstJob, portfolio))
        {
            Assert.Equal(HttpStatusCode.OK, firstCommit.StatusCode);
            using var body = JsonDocument.Parse(await firstCommit.Content.ReadAsStringAsync());
            Assert.Equal(1, body.RootElement.GetProperty("imported").GetInt32());
            Assert.Equal(0, body.RootElement.GetProperty("duplicates").GetInt32());
        }

        var secondJob = await Upload(client, owner, csv);
        using (var preview = await Preview(client, owner, secondJob, portfolio))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(1, body.RootElement.GetProperty("duplicates").GetInt32());
            var row = Assert.Single(body.RootElement.GetProperty("candidates").EnumerateArray());
            Assert.Equal("duplicate", row.GetProperty("status").GetString());
            Assert.Equal("external_key", row.GetProperty("reason").GetString());
        }

        using var secondCommit = await Commit(client, owner, secondJob, portfolio);
        Assert.Equal(HttpStatusCode.OK, secondCommit.StatusCode);
        using var secondBody = JsonDocument.Parse(await secondCommit.Content.ReadAsStringAsync());
        Assert.Equal(0, secondBody.RootElement.GetProperty("imported").GetInt32());
        Assert.Equal(1, secondBody.RootElement.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task PreviewFindsDuplicateRowsInsideTheUploadedFileWithoutTargetPortfolio()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "Datum;Typ;ISIN;Stück;Kurs;Betrag;Währung;ID\r\n" +
                           "30.08.2026;Kauf;DE000A1EWWW0;2;100,00;200,00;EUR;preview-file-1\r\n" +
                           "30.08.2026;Kauf;DE000A1EWWW0;2;100,00;200,00;EUR;preview-file-1\r\n";

        var job = await Upload(client, owner, csv);
        using var preview = await Preview(client, owner, job, null);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var body = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("duplicates").GetInt32());
        var rows = body.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("new", rows[0].GetProperty("status").GetString());
        Assert.Equal("duplicate", rows[1].GetProperty("status").GetString());
        Assert.Equal("in_file", rows[1].GetProperty("reason").GetString());
    }

    private static async Task<Guid> Upload(HttpClient client, Guid userId, string csv)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "broker.csv");
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            tradeDate = "Datum",
            tradeType = "Typ",
            settlementDate = (string?)null,
            securityName = (string?)null,
            isin = "ISIN",
            wkn = (string?)null,
            ticker = (string?)null,
            quantity = "Stück",
            price = "Kurs",
            grossAmount = (string?)null,
            amount = "Betrag",
            currency = "Währung",
            fees = (string?)null,
            taxes = (string?)null,
            withholdingTax = (string?)null,
            externalKey = "ID"
        }), Encoding.UTF8, "application/json"), "mapping");
        request.Content = form;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("errors").GetInt32());
        return document.RootElement.GetProperty("jobId").GetGuid();
    }

    private static async Task<HttpResponseMessage> Preview(
        HttpClient client, Guid userId, Guid jobId, Guid? portfolioId)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{jobId:D}/duplicate-preview?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        request.Content = JsonContent.Create(new
        {
            portfolioId,
            candidateIds = (Guid[]?)null
        });
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Commit(
        HttpClient client, Guid userId, Guid jobId, Guid portfolioId)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/investment-import/jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        request.Content = JsonContent.Create(new
        {
            portfolioId,
            securityMappings = new Dictionary<string, Guid?>(),
            createMissingSecurities = false,
            candidateIds = (Guid[]?)null
        });
        return await client.SendAsync(request);
    }

    private static async Task SeedOwner(BackendWebApplicationFactory factory, Guid owner)
    {
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Investment duplicate preview owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });
    }

    private static async Task SeedOwnerPortfolioSecurity(
        BackendWebApplicationFactory factory,
        Guid owner,
        Guid portfolio,
        Guid security)
    {
        await SeedOwner(factory, owner);
        await factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({security},{FullWorthSpaceDefaults.LegacyId},{"Import ETF"},{Isin},{"etf"},{"EUR"},{true},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolio},{FullWorthSpaceDefaults.LegacyId},{"Import Depot"},{"EUR"},{true},{true},{false},{now},{now})
""");
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
