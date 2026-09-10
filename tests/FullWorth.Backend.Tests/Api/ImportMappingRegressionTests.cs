using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

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

    // The review list can only be honest if the preview and the commit use the same detection. This
    // asserts the numbers against each other instead of against hand-written expectations, so the two
    // cannot drift apart without failing here.
    [Fact]
    public async Task DuplicatePreviewAgreesWithWhatTheCommitActuallySkips()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await SeedAccount(factory, account, owner);

        var already = await UploadAndCommit(client, owner, account,
            "Datum;Betrag;Empfänger;Text\r\n29.08.2026;-12,34;REWE;Lebensmittel\r\n30.08.2026;-5,00;Bäckerei;Frühstück\r\n");
        Assert.Equal(2, already.GetProperty("imported").GetInt32());

        // Two rows are already booked, one is new, and the last one repeats the new row inside the file.
        const string second = "Datum;Betrag;Empfänger;Text\r\n29.08.2026;-12,34;REWE;Lebensmittel\r\n"
            + "30.08.2026;-5,00;Bäckerei;Frühstück\r\n31.08.2026;-9,99;Kiosk;Zeitung\r\n31.08.2026;-9,99;Kiosk;Zeitung\r\n";
        using var upload = await Upload(client, owner, second, FullMapping());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");

        var preview = await Preview(client, owner, jobId, account);
        Assert.Equal(3, preview.GetProperty("duplicates").GetInt32());
        Assert.Equal(1, preview.GetProperty("fresh").GetInt32());
        var reasons = preview.GetProperty("candidates").EnumerateArray()
            .Where(item => item.GetProperty("status").GetString() == "duplicate")
            .Select(item => item.GetProperty("reason").GetString()).ToArray();
        // Rows this importer wrote itself carry a stable key, so they are caught by that key rather
        // than by the semantic comparison - the semantic path is covered separately below.
        Assert.Contains("external_key", reasons);
        Assert.Contains("in_file", reasons);

        // The preview must not write: the same rows against a different account mapping are a
        // different answer, so a stored status would be a guess about a decision not yet made.
        using var candidateRequest = UserRequest(HttpMethod.Get,
            $"/api/import-jobs/{jobId:D}/candidates?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        using var candidateResponse = await client.SendAsync(candidateRequest);
        Assert.Equal(HttpStatusCode.OK, candidateResponse.StatusCode);
        using var candidateDoc = JsonDocument.Parse(await candidateResponse.Content.ReadAsStringAsync());
        Assert.All(candidateDoc.RootElement.EnumerateArray(),
            item => Assert.Equal("new", item.GetProperty("duplicateStatus").GetString()));

        var commit = await Commit(client, owner, jobId, account);
        Assert.Equal(preview.GetProperty("fresh").GetInt32(), commit.GetProperty("imported").GetInt32());
        Assert.Equal(preview.GetProperty("duplicates").GetInt32(), commit.GetProperty("duplicates").GetInt32());
    }

    // A booking that came from a bank sync or was typed in by hand has no import key, so only the
    // semantic comparison can recognise it. That is the case that protects against double-booking
    // an export of something already in the account.
    [Fact]
    public async Task DuplicatePreviewRecognisesABookingThatWasNotCreatedByAnImport()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await SeedAccount(factory, account, owner);
        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = account,
                ExternalKey = $"fints-{Guid.NewGuid():N}",
                BookingDate = new DateOnly(2026, 8, 29),
                ValueDate = new DateOnly(2026, 8, 29),
                Amount = -12.34m,
                Currency = "EUR",
                Counterparty = "REWE",
                NormalizedCounterparty = MerchantNormalization.Normalize("REWE")
            });
            await db.SaveChangesAsync();
        });

        using var upload = await Upload(client, owner,
            "Datum;Betrag;Empfänger;Text\r\n29.08.2026;-12,34;REWE;Lebensmittel\r\n", FullMapping());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");

        var preview = await Preview(client, owner, jobId, account);
        Assert.Equal(1, preview.GetProperty("duplicates").GetInt32());
        Assert.Equal(0, preview.GetProperty("fresh").GetInt32());
        Assert.Equal("existing", preview.GetProperty("candidates").EnumerateArray()
            .Single().GetProperty("reason").GetString());
    }

    // categoryMappings was supported by the commit but no caller ever sent one, so it was untested.
    // Now that the review step offers the mapping, both directions have to hold: an explicit target
    // beats the same-named category, and an explicit "do not map" suppresses the name match too.
    [Fact]
    public async Task AnExplicitCategoryMappingWinsOverTheSameNamedCategory()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        var leisure = Guid.NewGuid();
        var other = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await SeedAccount(factory, account, owner);
        await factory.SeedAsync(async db =>
        {
            db.Categories.Add(new FinanceCategory
            {
                Id = leisure, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = "test-freizeit", Name = "Freizeit", IsSystem = false, IsArchived = false,
                SortOrder = 0, CreatedAt = DateTimeOffset.UtcNow
            });
            db.Categories.Add(new FinanceCategory
            {
                Id = other, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = "test-sonstiges", Name = "Sonstiges", IsSystem = false, IsArchived = false,
                SortOrder = 0, CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        });

        using var upload = await Upload(client, owner,
            "Datum;Betrag;Empfänger;Kategorie\r\n29.08.2026;-12,34;REWE;Sonstiges\r\n30.08.2026;-5,00;Kino;Freizeit\r\n",
            new
            {
                date = "Datum", amount = "Betrag", currency = (string?)null, counterparty = "Empfänger",
                description = (string?)null, account = (string?)null, category = "Kategorie",
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
            categoryMappings = new Dictionary<string, Guid?> { ["Sonstiges"] = leisure, ["Freizeit"] = null },
            createMissingCategories = false,
            runFullWorthCategorization = false,
            candidateIds = (Guid[]?)null
        });
        using var response = await client.SendAsync(commit);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var imported = await db.Transactions.AsNoTracking()
                .Where(t => t.AccountId == account).OrderBy(t => t.BookingDate).ToListAsync();
            Assert.Equal(2, imported.Count);
            Assert.Equal(leisure, imported[0].CategoryId);
            Assert.Null(imported[1].CategoryId);
            // Nothing new was invented: createMissingCategories was off and both names already existed.
            Assert.Equal(2, await db.Categories.AsNoTracking()
                .CountAsync(c => c.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId && !c.IsSystem));
        });
    }

    // A dot as the decimal separator is the normal shape of an English-format CSV and the ONLY shape
    // an .xlsx can have, because OOXML stores cell values invariant. Parsing it with a German culture
    // that allows thousands grouping turns 1234.56 into 123456, and .NET does not validate group sizes,
    // so the wrong value parses successfully and is committed as real money.
    [Fact]
    public async Task DotDecimalAmountsAreImportedAtTheirRealValue()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await SeedAccount(factory, account, owner);

        using var upload = await Upload(client, owner,
            "Datum;Betrag;Empfänger\r\n2026-08-29;1234.56;Gehalt\r\n2026-08-30;-0.99;Kaffee\r\n2026-08-31;-1234,56;Miete\r\n",
            new
            {
                date = "Datum", amount = "Betrag", currency = (string?)null, counterparty = "Empfänger",
                description = (string?)null, account = (string?)null, category = (string?)null,
                externalKey = (string?)null
            });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");

        await Commit(client, owner, jobId, account);

        await factory.SeedAsync(async db =>
        {
            var imported = await db.Transactions.AsNoTracking()
                .Where(t => t.AccountId == account).OrderBy(t => t.BookingDate).ToListAsync();
            Assert.Equal(3, imported.Count);
            // Both notations must survive, and neither may be inflated by a factor of 100.
            Assert.Equal(1234.56m, imported[0].Amount);
            Assert.Equal(-0.99m, imported[1].Amount);
            Assert.Equal(-1234.56m, imported[2].Amount);
        });
    }

    // Same parser, the other endpoint pair: the unmapped generic importer.
    [Theory]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-0.99", -0.99)]
    [InlineData("0,5", 0.5)]
    [InlineData("1.234.567,89", 1234567.89)]
    public async Task GenericImportReadsBothDecimalNotations(string amount, decimal expected)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await SeedAccount(factory, account, owner);

        using var upload = await Upload(client, owner,
            $"Datum;Betrag;Empfänger\r\n2026-08-29;{amount};Test\r\n",
            new
            {
                date = "Datum", amount = "Betrag", currency = (string?)null, counterparty = "Empfänger",
                description = (string?)null, account = (string?)null, category = (string?)null,
                externalKey = (string?)null
            });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");
        await Commit(client, owner, jobId, account);

        await factory.SeedAsync(async db =>
        {
            var imported = await db.Transactions.AsNoTracking()
                .SingleAsync(t => t.AccountId == account);
            Assert.Equal(expected, imported.Amount);
        });
    }

    private static async Task<JsonElement> Preview(HttpClient client, Guid owner, Guid jobId, Guid account)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/jobs/{jobId:D}/duplicate-preview?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            sourceAccountMappings = new Dictionary<string, Guid?>(),
            defaultAccountId = account
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> Commit(HttpClient client, Guid owner, Guid jobId, Guid account)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            sourceAccountMappings = new Dictionary<string, Guid?>(),
            defaultAccountId = account,
            categoryMappings = new Dictionary<string, Guid?>(),
            createMissingCategories = false,
            runFullWorthCategorization = false,
            candidateIds = (Guid[]?)null
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static object FullMapping() => new
    {
        date = "Datum",
        amount = "Betrag",
        currency = (string?)null,
        counterparty = "Empfänger",
        description = "Text",
        account = (string?)null,
        category = (string?)null,
        externalKey = (string?)null
    };

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

    // The importer answered "EUR" for anything it could not read in a mapped currency column, so a
    // foreign amount landed in a euro column and was later converted 1:1. A stated-but-unreadable
    // currency is a row error the user can see instead.
    [Fact]
    public async Task An_unreadable_currency_makes_the_row_an_error_instead_of_euro()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);

        const string csv = "Datum;Betrag;Währung;Empfänger\r\n" +
                           "30.08.2026;-12,34;EUR;REWE\r\n" +
                           "30.08.2026;-99,00;Dollars;Broken\r\n";
        using var response = await Upload(client, owner, csv, new
        {
            date = "Datum",
            amount = "Betrag",
            currency = "Währung",
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

    // A file with no currency column states no currency. Stamping EUR mislabelled every row of a space
    // that is not held in euro; the space's own base currency is the honest reading.
    [Fact]
    public async Task Without_a_currency_column_the_space_currency_is_used()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        await SeedOwner(factory, owner);
        await factory.SeedAsync(async db =>
        {
            var space = await db.FullWorthSpaces.SingleAsync(x => x.Id == FullWorthSpaceDefaults.LegacyId);
            space.BaseCurrency = "USD";
            await db.SaveChangesAsync();
        });

        using var upload = await Upload(client, owner,
            "Datum;Betrag;Empfänger\r\n30.08.2026;-12,34;REWE\r\n",
            BasicMapping());
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var jobId = ReadGuid(await upload.Content.ReadAsStringAsync(), "jobId");

        using var request = UserRequest(HttpMethod.Get,
            $"/api/import-jobs/{jobId:D}/candidates?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var row = doc.RootElement.EnumerateArray().Single();
        Assert.Equal("USD", row.GetProperty("currency").GetString());
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
