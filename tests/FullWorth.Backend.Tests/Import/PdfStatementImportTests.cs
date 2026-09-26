using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Documents;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Ein PDF-Kontoauszug laeuft durch denselben Weg wie MT940 und CAMT (#131, Abschnitt 11): Erkennung,
/// Vorschau, Festschreiben, Kontostand. Das Werkzeug, das die Worte liest (poppler), ist hier durch
/// einen festen Auszug ersetzt - geprueft wird, was der Import daraus macht, nicht poppler.
/// </summary>
public sealed class PdfStatementImportTests
{
    // Die ersten Bytes eines PDF - mehr braucht die Erkennung nicht, den Inhalt liefert der Fake.
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% erfunden\n");

    private sealed class FixedPdf(IReadOnlyList<IReadOnlyList<PdfLine>> pages) : IPdfWordSource
    {
        public Task<IReadOnlyList<IReadOnlyList<PdfLine>>> ReadLinesAsync(byte[] pdf, CancellationToken ct) =>
            Task.FromResult(pages);
    }

    [Fact]
    public async Task ThePdfIsRecognisedAsAStatementNotABrokerStatement()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var request = UserRequest(HttpMethod.Post, $"/api/import/detect?{Space}", user);
        request.Content = FileContent();
        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("statement", doc.RootElement.GetProperty("adapter").GetString());
        Assert.Equal("ikano_pdf", doc.RootElement.GetProperty("reasonKey").GetString());
    }

    /// <summary>
    /// Die Pruefnotizen kommen bei der Vorschau an, und uebernommen wird, was der Nutzer stehen laesst -
    /// hier genau die Zeilen, die die Seite vorwaehlt: die echten Bewegungen, ohne die internen
    /// Umbuchungen. Der Kontostand des Auszugs verankert das Konto mit seinem Datum.
    /// </summary>
    [Fact]
    public async Task ThePreselectedRowsAndTheBalanceArriveOnTheAccount()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        var account = await SeedAccountAsync(factory, user);
        using var client = factory.CreateClient();

        using var upload = UserRequest(HttpMethod.Post, $"/api/import-jobs/upload?{Space}", user);
        upload.Content = FileContent();
        using var uploaded = await client.SendAsync(upload);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        using var uploadDoc = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var jobId = uploadDoc.RootElement.GetProperty("jobId").GetGuid();
        Assert.Equal("ikano_pdf", uploadDoc.RootElement.GetProperty("adapter").GetString());
        Assert.Empty(uploadDoc.RootElement.GetProperty("warnings").EnumerateArray());

        using var candidatesRequest = UserRequest(HttpMethod.Get, $"/api/import-jobs/{jobId:D}/candidates?{Space}", user);
        using var candidatesResponse = await client.SendAsync(candidatesRequest);
        using var candidates = JsonDocument.Parse(await candidatesResponse.Content.ReadAsStringAsync());
        var rows = candidates.RootElement.EnumerateArray().ToList();
        Assert.Equal(7, rows.Count);
        var preselected = rows
            .Where(row => row.GetProperty("reviewNote").ValueKind == JsonValueKind.Null)
            .Select(row => row.GetProperty("id").GetGuid())
            .ToList();
        Assert.Equal(3, preselected.Count);
        Assert.All(rows.Where(row => row.GetProperty("reviewNote").ValueKind != JsonValueKind.Null),
            row => Assert.Equal("internal_transfer", row.GetProperty("reviewNote").GetString()));

        using var commit = UserRequest(HttpMethod.Post, $"/api/import-jobs/{jobId:D}/commit?{Space}", user);
        commit.Content = JsonContent.Create(new { accountId = account, candidateIds = preselected });
        using var committed = await client.SendAsync(commit);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var written = await db.Transactions.AsNoTracking().Where(item => item.AccountId == account).ToListAsync();
            Assert.Equal(3, written.Count);
            Assert.Equal(-50m, written.Sum(item => item.Amount));

            var balance = await db.BalanceSnapshots.AsNoTracking().SingleAsync(item => item.AccountId == account);
            Assert.Equal(-1_050m, balance.Amount);
            Assert.Equal(new DateOnly(2026, 9, 23), balance.ReferenceDate);
            Assert.Equal(BalanceSources.Import, balance.Source);
        });
    }

    /// <summary>
    /// #131, Paritaetstest 4: ein Ikano-PDF mit datiertem Schlussstand. Nach dem Import ist das Konto
    /// ein Konto wie jedes andere - die Kontenliste zeigt den Stand mit SEINEM Datum und seiner Quelle,
    /// und das Vermoegen zaehlt genau diesen Betrag, ohne Verknuepfung, ohne Zwischenschritt.
    /// </summary>
    [Fact]
    public async Task AnImportedStatementBalanceReachesAccountsAndNetWorthWithItsDate()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        var account = await SeedAccountAsync(factory, user);
        using var client = factory.CreateClient();
        await ImportPreselectedAsync(client, user, account);

        using var accountsRequest = UserRequest(HttpMethod.Get, $"/api/accounts?{Space}", user);
        using var accountsResponse = await client.SendAsync(accountsRequest);
        accountsResponse.EnsureSuccessStatusCode();
        using var accounts = JsonDocument.Parse(await accountsResponse.Content.ReadAsStringAsync());
        var row = accounts.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == account);
        var latest = row.GetProperty("latestBalance");
        Assert.Equal(-1_050m, latest.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-09-23", latest.GetProperty("referenceDate").GetString()![..10]);
        Assert.Equal(BalanceSources.Import, latest.GetProperty("source").GetString());

        using var overviewRequest = UserRequest(HttpMethod.Get, $"/api/wealth/overview?{Space}", user);
        using var overviewResponse = await client.SendAsync(overviewRequest);
        overviewResponse.EnsureSuccessStatusCode();
        using var overview = JsonDocument.Parse(await overviewResponse.Content.ReadAsStringAsync());
        Assert.Equal(-1_050m, overview.RootElement.GetProperty("accounts").GetProperty("amount").GetDecimal());
        Assert.Equal(-1_050m, overview.RootElement.GetProperty("netWorth").GetDecimal());
        Assert.True(overview.RootElement.GetProperty("isComplete").GetBoolean());
    }

    /// <summary>
    /// #131, Paritaetstest 2: dieselbe reale Buchung aus zwei Quellen. Finanzguru hat die Moebelhaus-
    /// Zahlung schon gebracht, unter ihrem eigenen Namen; der Auszug bringt sie noch einmal, so wie die
    /// Bank sie schreibt. Gleiches Konto, gleicher Tag, gleicher Betrag - das darf nicht still eine
    /// zweite Buchung werden.
    /// </summary>
    [Fact]
    public async Task TheSameBookingFromAnotherSourceIsNotBookedTwice()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        var account = await SeedAccountAsync(factory, user);
        await SeedFinanzguruFurnitureAsync(factory, account);
        using var client = factory.CreateClient();

        await ImportPreselectedAsync(client, user, account);

        await factory.SeedAsync(async db =>
        {
            var furniture = await db.Transactions.AsNoTracking()
                .Where(item => item.AccountId == account && item.Amount == -120m)
                .ToListAsync();
            Assert.Single(furniture);
        });
    }

    /// <summary>
    /// Die vermutliche Dublette ist ein Vorschlag, keine Sperre: die Vorschau nennt die vorhandene
    /// Buchung, und wer die Zeile trotzdem anhakt - zwei echte gleich hohe Zahlungen am selben Tag gibt
    /// es -, bekommt sie gebucht.
    /// </summary>
    [Fact]
    public async Task AProbableDuplicateNamesTheExistingBookingAndIsBookedWhenTicked()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        var account = await SeedAccountAsync(factory, user);
        await SeedFinanzguruFurnitureAsync(factory, account);
        using var client = factory.CreateClient();

        await ImportPreselectedAsync(client, user, account, alsoProbable: true);

        await factory.SeedAsync(async db => Assert.Equal(2,
            await db.Transactions.AsNoTracking().CountAsync(item => item.AccountId == account && item.Amount == -120m)));
    }

    [Fact]
    public async Task ThePreviewSaysWhichBookingARowProbablyDuplicates()
    {
        using var factory = Factory();
        var user = await SeedAsync(factory);
        var account = await SeedAccountAsync(factory, user);
        await SeedFinanzguruFurnitureAsync(factory, account);
        using var client = factory.CreateClient();

        using var upload = UserRequest(HttpMethod.Post, $"/api/import-jobs/upload?{Space}", user);
        upload.Content = FileContent();
        using var uploaded = await client.SendAsync(upload);
        using var uploadDoc = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var jobId = uploadDoc.RootElement.GetProperty("jobId").GetGuid();

        using var request = UserRequest(HttpMethod.Get, $"/api/import-jobs/{jobId:D}/candidates?{Space}&accountId={account:D}", user);
        using var response = await client.SendAsync(request);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var furniture = doc.RootElement.EnumerateArray().Single(row => row.GetProperty("amount").GetDecimal() == -120m
            && row.GetProperty("reviewNote").ValueKind == JsonValueKind.Null);
        Assert.Equal("probable", furniture.GetProperty("duplicate").GetString());
        Assert.Equal("Möbelhaus Beispiel GmbH", furniture.GetProperty("duplicateOf").GetProperty("counterparty").GetString());
        Assert.Equal("2026-09-11", furniture.GetProperty("duplicateOf").GetProperty("bookingDate").GetString());
        // Die anderen Zeilen haben auf dem Konto nichts, womit sie verwechselt werden koennten.
        Assert.Single(doc.RootElement.EnumerateArray(), row => row.GetProperty("duplicate").ValueKind != JsonValueKind.Null);

        // Ohne Zielkonto gibt es nichts zu vergleichen - und ein fremdes Konto verraet nichts.
        using var foreign = UserRequest(HttpMethod.Get, $"/api/import-jobs/{jobId:D}/candidates?{Space}&accountId={Guid.NewGuid():D}", user);
        using var foreignResponse = await client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.BadRequest, foreignResponse.StatusCode);
    }

    /// <summary>Die Moebelhaus-Zahlung, wie Finanzguru sie schon gebracht hat - unter ihrem eigenen Namen.</summary>
    private static Task SeedFinanzguruFurnitureAsync(BackendWebApplicationFactory factory, Guid account) =>
        factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FullWorth.Backend.Modules.Transactions.FinanceTransaction
            {
                Id = Guid.NewGuid(),
                AccountId = account,
                ExternalKey = "finanzguru:4711",
                BookingDate = new DateOnly(2026, 9, 11),
                Amount = -120m,
                Currency = "EUR",
                Counterparty = "Möbelhaus Beispiel GmbH",
                NormalizedCounterparty = FullWorth.Backend.Modules.Merchants.MerchantNormalization.Normalize("Möbelhaus Beispiel GmbH"),
                Status = "BOOK"
            });
            await db.SaveChangesAsync();
        });

    /// <summary>Hochladen und genau die vorgewaehlten Zeilen festschreiben - wie die Seite es tut.</summary>
    private static async Task ImportPreselectedAsync(HttpClient client, Guid user, Guid account, bool alsoProbable = false)
    {
        using var upload = UserRequest(HttpMethod.Post, $"/api/import-jobs/upload?{Space}", user);
        upload.Content = FileContent();
        using var uploaded = await client.SendAsync(upload);
        uploaded.EnsureSuccessStatusCode();
        using var uploadDoc = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var jobId = uploadDoc.RootElement.GetProperty("jobId").GetGuid();

        // Mit dem Zielkonto, wie die Seite sie laedt, sobald es gewaehlt ist.
        using var candidatesRequest = UserRequest(HttpMethod.Get, $"/api/import-jobs/{jobId:D}/candidates?{Space}&accountId={account:D}", user);
        using var candidatesResponse = await client.SendAsync(candidatesRequest);
        candidatesResponse.EnsureSuccessStatusCode();
        using var candidates = JsonDocument.Parse(await candidatesResponse.Content.ReadAsStringAsync());
        var preselected = candidates.RootElement.EnumerateArray()
            .Where(row => row.GetProperty("reviewNote").ValueKind == JsonValueKind.Null)
            .Where(row => row.GetProperty("duplicate").ValueKind == JsonValueKind.Null
                || (alsoProbable && row.GetProperty("duplicate").GetString() == "probable"))
            .Select(row => row.GetProperty("id").GetGuid())
            .ToList();

        using var commit = UserRequest(HttpMethod.Post, $"/api/import-jobs/{jobId:D}/commit?{Space}", user);
        commit.Content = JsonContent.Create(new { accountId = account, candidateIds = preselected });
        using var committed = await client.SendAsync(commit);
        committed.EnsureSuccessStatusCode();
    }

    private static readonly string Space = $"fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}";

    private static BackendWebApplicationFactory Factory() =>
        new(new Dictionary<string, string?>(), services =>
            services.AddSingleton<IPdfWordSource>(new FixedPdf(BankStatementPdfTests.Ikano())));

    private static MultipartFormDataContent FileContent()
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", "abrechnung.pdf");
        return content;
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Guid> SeedAsync(BackendWebApplicationFactory factory)
    {
        var user = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = user, EmailNormalized = $"{user:N}@LOCAL.TEST", DisplayName = "Owner", IsActive = true });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });
        return user;
    }

    private static async Task<Guid> SeedAccountAsync(BackendWebApplicationFactory factory, Guid user)
    {
        var account = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{account:N}",
                IdentificationHash = $"manual-{account:N}",
                InstitutionName = "Ikano Bank",
                DisplayName = "IKEA Kreditkarte",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner { AccountId = account, UserId = user, OwnershipType = AccountOwnershipTypes.Owner });
            await db.SaveChangesAsync();
        });
        return account;
    }
}
