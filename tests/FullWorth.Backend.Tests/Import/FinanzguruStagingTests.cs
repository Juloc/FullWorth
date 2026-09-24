using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FullWorth.Backend.Tests.Import.FinanzguruWorkbook;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Der Zwischenschritt des Finanzguru-Imports (#131, Schritt 4).
///
/// Was diese Tests halten, ist eine einzige Zusage: <b>die Vorschau sagt, was passieren wird.</b>
/// Eine Vorschau, die nach anderen Regeln zuordnet oder anders entdoppelt als das Festschreiben,
/// waere schlimmer als gar keine - sie wuerde eine Entscheidung einholen, die sich auf etwas
/// bezieht, das so nicht eintritt.
/// </summary>
public sealed class FinanzguruStagingTests
{
    [Fact]
    public async Task StagingWritesNoTransactionUntilCommit()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var preview = await StageAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "stage-1"),
            Row("27.08.2026", -20m, "Baumarkt", "Einkauf", "Wohnen", "Haushalt", "stage-2")));

        Assert.Equal(2, preview.SourceRows);
        Assert.Equal(2, preview.NewRows);
        Assert.Equal(0, preview.AlreadyImported);
        Assert.Equal(new DateOnly(2026, 8, 27), preview.From);
        Assert.Equal(new DateOnly(2026, 8, 28), preview.To);
        var account = Assert.Single(preview.Accounts);
        Assert.Equal("linked", account.Status);
        Assert.Equal(scenario.Account, account.AccountId);
        Assert.Equal(2, account.Rows);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.Transactions.CountAsync());
            // Der Auftrag existiert schon - aber als Zwischenstand, nicht als erledigter Import.
            var job = await db.Database.SqlQuery<string>(
                $"""SELECT "Status" AS "Value" FROM "ImportJobs" WHERE "Id" = {preview.JobId}""").SingleAsync();
            Assert.Equal("ready", job);
            Assert.Equal(2, await db.Database.SqlQuery<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM "ImportCandidates" WHERE "ImportJobId" = {preview.JobId}""").SingleAsync());
        });

        var result = await CommitAsync(client, scenario, preview.JobId, null);
        Assert.Equal(2, result.TransactionsImported);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(2, await db.Transactions.CountAsync(tx => tx.AccountId == scenario.Account));
            var job = await db.Database.SqlQuery<string>(
                $"""SELECT "Status" AS "Value" FROM "ImportJobs" WHERE "Id" = {preview.JobId}""").SingleAsync();
            Assert.Equal("completed", job);
        });
    }

    [Fact]
    public async Task OnlyTheChosenRowsAreWritten()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var preview = await StageAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "pick-1"),
            Row("27.08.2026", -20m, "Baumarkt", "Einkauf", "Wohnen", "Haushalt", "pick-2"),
            Row("26.08.2026", -30m, "Tankstelle", "Sprit", "Mobilitaet", "Auto", "pick-3")));
        Assert.Equal(3, preview.NewRows);

        var chosen = await CandidateIdsAsync(factory, preview.JobId, "pick-1", "pick-3");
        var result = await CommitAsync(client, scenario, preview.JobId, chosen);

        Assert.Equal(2, result.TransactionsImported);
        // Die Quellzeilenzahl bleibt die der DATEI. "2 von 2" waere eine andere und falsche Auskunft
        // ueber denselben Vorgang.
        Assert.Equal(3, result.SourceRows);

        await factory.SeedAsync(async db =>
        {
            var keys = await db.Transactions.Select(tx => tx.ExternalKey).OrderBy(key => key).ToListAsync();
            Assert.Equal(["finanzguru:pick-1", "finanzguru:pick-3"], keys);
        });
    }

    [Fact]
    public async Task SplitChildrenFollowTheirParentRow()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var preview = await StageAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "plain-1"),
            Row("27.08.2026", -30m, "Amazon", "Bestellung", "Lifestyle", "Shopping", "split-original", splitType: "Original"),
            Row("27.08.2026", -10m, "Amazon", "Bestellung", "Lifestyle", "Shopping", "split-a", "split-original", "Teilbuchung"),
            Row("27.08.2026", -20m, "Amazon", "Bestellung", "Wohnen", "Haushalt", "split-b", "split-original", "Restbetrag")));

        // Die Aufteilungen sind keine waehlbaren Zeilen: sie sind Teile einer einzigen Buchung.
        Assert.Equal(4, preview.SourceRows);
        Assert.Equal(2, preview.NewRows);

        var chosen = await CandidateIdsAsync(factory, preview.JobId, "split-original");
        var result = await CommitAsync(client, scenario, preview.JobId, chosen);

        Assert.Equal(1, result.TransactionsImported);
        Assert.Equal(1, result.SplitTransactions);

        await factory.SeedAsync(async db =>
        {
            var transaction = Assert.Single(await db.Transactions.ToListAsync());
            Assert.Equal("finanzguru:split-original", transaction.ExternalKey);
            var allocations = await db.TransactionAllocations.Where(item => item.TransactionId == transaction.Id).ToListAsync();
            Assert.Equal(2, allocations.Count);
            Assert.Equal(-30m, allocations.Sum(item => item.Amount));
        });
    }

    /// <summary>
    /// Die Kernzusage. Der Abgleich gegen eine Bankbuchung ist die Stelle, an der eine eigene
    /// Vorschau-Logik am ehesten auseinanderliefe - deshalb zaehlt hier, dass beide Seiten
    /// dieselbe Zahl nennen und dass die abgeglichene Zeile auch beim Festschreiben nicht doppelt
    /// im Konto landet.
    /// </summary>
    [Fact]
    public async Task PreviewCountsAreWhatTheCommitDoes()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, addExistingTransaction: true);
        using var client = factory.CreateClient();

        var workbook = Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "match-1"),
            Row("27.08.2026", -20m, "Baumarkt", "Einkauf", "Wohnen", "Haushalt", "match-2"));

        var preview = await StageAsync(client, scenario, workbook);
        Assert.Equal(1, preview.MatchedExisting);
        Assert.Equal(1, preview.NewRows);

        var result = await CommitAsync(client, scenario, preview.JobId, null);
        Assert.Equal(preview.NewRows, result.TransactionsImported);
        Assert.Equal(preview.MatchedExisting, result.MatchedExistingTransactions);
        Assert.Equal(preview.AlreadyImported, result.AlreadyImported);
        Assert.Equal(preview.SourceRows, result.SourceRows);

        await factory.SeedAsync(async db =>
        {
            // Die Bankbuchung und die eine neue Zeile - die abgeglichene ist nicht ein zweites Mal da.
            Assert.Equal(2, await db.Transactions.CountAsync(tx => tx.AccountId == scenario.Account));
        });
    }

    [Fact]
    public async Task ASecondStageOfTheSameFileSeesTheFirstImport()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var workbook = Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "again-1"),
            Row("27.08.2026", -20m, "Baumarkt", "Einkauf", "Wohnen", "Haushalt", "again-2"));

        var first = await StageAsync(client, scenario, workbook);
        await CommitAsync(client, scenario, first.JobId, null);

        var second = await StageAsync(client, scenario, workbook);
        Assert.Equal(0, second.NewRows);
        Assert.Equal(2, second.AlreadyImported);

        var result = await CommitAsync(client, scenario, second.JobId, null);
        Assert.Equal(0, result.TransactionsImported);
        Assert.Equal(2, result.AlreadyImported);

        await factory.SeedAsync(async db =>
            Assert.Equal(2, await db.Transactions.CountAsync(tx => tx.AccountId == scenario.Account)));
    }

    /// <summary>
    /// Die gelesene Datei liegt bis zum Festschreiben am Auftrag - danach nicht mehr. Ein zweites
    /// Festschreiben desselben Auftrags hat nichts mehr zu schreiben und sagt das, statt es still
    /// ein zweites Mal zu tun.
    /// </summary>
    [Fact]
    public async Task CommitTwiceIsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var preview = await StageAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "once-1")));
        await CommitAsync(client, scenario, preview.JobId, null);

        using var again = await SendCommitAsync(client, scenario, preview.JobId, null);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Transactions.CountAsync());
            var payloads = await db.Database.SqlQuery<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM "ImportJobs" WHERE "Id" = {preview.JobId} AND "SourcePayloadEncrypted" IS NOT NULL""").SingleAsync();
            Assert.Equal(0, payloads);
        });
    }

    [Fact]
    public async Task StagingForAnotherUsersSpaceIsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await SendStageAsync(client, scenario.Space, scenario.Outsider, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "outsider-1")));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Equal(0, await db.Database.SqlQuery<int>(
                $"""SELECT COUNT(*)::int AS "Value" FROM "ImportJobs" """).SingleAsync()));
    }

    private static async Task<FinanzguruStagePreview> StageAsync(HttpClient client, Scenario scenario, byte[] workbook)
    {
        using var response = await SendStageAsync(client, scenario.Space, scenario.User, workbook);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<FinanzguruStagePreview>();
        Assert.NotNull(preview);
        return preview!;
    }

    private static async Task<FinanzguruImportResult> CommitAsync(
        HttpClient client, Scenario scenario, Guid jobId, IReadOnlyList<Guid>? candidateIds)
    {
        using var response = await SendCommitAsync(client, scenario, jobId, candidateIds);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FinanzguruImportResult>();
        Assert.NotNull(result);
        return result!;
    }

    private static async Task<HttpResponseMessage> SendStageAsync(
        HttpClient client, Guid fullWorthSpaceId, Guid userId, byte[] workbook)
    {
        var request = Authenticated(HttpMethod.Post,
            $"/api/import/finanzguru/stage?fullWorthSpaceId={fullWorthSpaceId:D}", userId);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(workbook);
        file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "finanzguru.xlsx");
        request.Content = form;
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendCommitAsync(
        HttpClient client, Scenario scenario, Guid jobId, IReadOnlyList<Guid>? candidateIds)
    {
        var request = Authenticated(HttpMethod.Post,
            $"/api/import/finanzguru/jobs/{jobId:D}/commit?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        request.Content = JsonContent.Create(new { candidateIds });
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string url, Guid userId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    /// <summary>Die Kandidatenkennungen zu den genannten Finanzguru-Buchungskennungen.</summary>
    private static async Task<List<Guid>> CandidateIdsAsync(
        BackendWebApplicationFactory factory, Guid jobId, params string[] bookingIds)
    {
        List<Guid> ids = [];
        await factory.SeedAsync(async db =>
        {
            ids = await db.Database.SqlQuery<Guid>(
                $"""
                 SELECT "Id" AS "Value" FROM "ImportCandidates"
                 WHERE "ImportJobId" = {jobId} AND "RowFingerprint" = ANY({bookingIds})
                 """).ToListAsync();
        });
        Assert.Equal(bookingIds.Length, ids.Count);
        return ids;
    }

    private sealed record Scenario(Guid Space, Guid User, Guid Outsider, Guid Account);

    private static async Task<Scenario> SeedAsync(
        BackendWebApplicationFactory factory, bool addExistingTransaction = false)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.AddRange(
                new FullWorthUser { Id = scenario.User, EmailNormalized = $"{scenario.User:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Import owner", IsActive = true },
                new FullWorthUser { Id = scenario.Outsider, EmailNormalized = $"{scenario.Outsider:N}@EXAMPLE.COM".ToUpperInvariant(), DisplayName = "Outsider", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "Import space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.User, Role = FullWorthSpaceRoles.Owner });

            var connectionId = Guid.NewGuid();
            db.BankConnections.Add(new BankConnection
            {
                Id = connectionId,
                FullWorthSpaceId = scenario.Space,
                Provider = "enable-banking",
                InstitutionName = "Bank",
                Country = "DE",
                ProviderSessionId = $"session-{connectionId:N}",
                Status = "AUTHORIZED"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = scenario.Account,
                FullWorthSpaceId = scenario.Space,
                BankConnectionId = connectionId,
                Provider = "enable-banking",
                IdentificationHash = $"hash-{scenario.Account:N}",
                ProviderAccountId = $"provider-{scenario.Account:N}",
                InstitutionName = "Bank",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IbanLast4 = "1426"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = scenario.Account,
                UserId = scenario.User,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            if (addExistingTransaction)
            {
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = scenario.Account,
                    ExternalKey = "enable-banking:existing",
                    Status = "BOOK",
                    BookingDate = new DateOnly(2026, 8, 28),
                    ValueDate = new DateOnly(2026, 8, 28),
                    Amount = -10m,
                    Currency = "EUR",
                    Counterparty = "Supermarkt",
                    NormalizedCounterparty = "SUPERMARKT",
                    Description = "Provider text",
                    RawJson = "{}"
                });
            }

            await db.SaveChangesAsync();
        });
        return scenario;
    }
}
