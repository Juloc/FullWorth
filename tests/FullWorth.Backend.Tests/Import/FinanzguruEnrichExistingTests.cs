using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FullWorth.Backend.Tests.Import.FinanzguruWorkbook;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Dieselbe reale Buchung aus zwei Quellen (#131, Abschnitt 6/7).
///
/// Die Bank liefert Datum, Betrag und Gegenpartei. Finanzguru liefert dieselbe Buchung noch einmal,
/// dazu aber eine Kategorie, ihre Aufteilung und die Umbuchungskennzeichnung. Bis hierher wurde diese
/// zweite Zeile gezaehlt und weggeworfen: der Nutzer sah "41 abgeglichen" und hatte danach genauso
/// wenig Kategorien wie vorher.
///
/// Die Grenze, die diese Tests halten: ergaenzt wird nur, wo noch niemand etwas entschieden hat, und
/// zurueckgenommen wird nur der eigene Beitrag - nie die Buchung selbst, die einer anderen Quelle
/// gehoert.
/// </summary>
public sealed class FinanzguruEnrichExistingTests
{
    [Fact]
    public async Task AMatchedBankTransactionGetsTheSourcesCategory()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "enrich-1")));

        Assert.Equal(0, result.TransactionsImported);
        Assert.Equal(1, result.MatchedExistingTransactions);
        Assert.Equal(1, result.EnrichedExistingTransactions);

        await factory.SeedAsync(async db =>
        {
            var transaction = Assert.Single(await db.Transactions.ToListAsync());
            // Die Buchung der Bank, nicht eine zweite daneben.
            Assert.Equal("enable-banking:existing", transaction.ExternalKey);
            Assert.NotNull(transaction.CategoryId);
            Assert.Equal("finanzguru", transaction.CategorizationSource);
        });
    }

    [Fact]
    public async Task AMatchedBankTransactionGetsTheSourcesSplitAndTransferFlag()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "split-original", splitType: "Original", isTransfer: true),
            Row("28.08.2026", -4m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "split-a", "split-original", "Teilbuchung"),
            Row("28.08.2026", -6m, "Supermarkt", "Einkauf", "Wohnen", "Haushalt", "split-b", "split-original", "Restbetrag")));

        Assert.Equal(1, result.EnrichedExistingTransactions);
        Assert.Equal(1, result.SplitTransactions);

        await factory.SeedAsync(async db =>
        {
            var transaction = Assert.Single(await db.Transactions.ToListAsync());
            Assert.True(transaction.IsTransfer);
            // Eine aufgeteilte Buchung traegt ihre Kategorie in den Teilen, nicht an sich selbst.
            Assert.Null(transaction.CategoryId);
            var allocations = await db.TransactionAllocations.Where(item => item.TransactionId == transaction.Id).ToListAsync();
            Assert.Equal(2, allocations.Count);
            Assert.Equal(-10m, allocations.Sum(item => item.Amount));
        });
    }

    /// <summary>
    /// Die Grenze. Eine Kategorie, die der Nutzer gesetzt hat, traegt <c>"manual"</c> - und bleibt.
    /// </summary>
    [Fact]
    public async Task AUserCategorisedTransactionIsNotTouched()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        var ownCategory = await CategoriseByHandAsync(factory, scenario);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "enrich-1")));

        Assert.Equal(1, result.MatchedExistingTransactions);
        Assert.Equal(0, result.EnrichedExistingTransactions);

        await factory.SeedAsync(async db =>
        {
            var transaction = Assert.Single(await db.Transactions.ToListAsync());
            Assert.Equal(ownCategory, transaction.CategoryId);
            Assert.Equal("manual", transaction.CategorizationSource);
        });
    }

    /// <summary>
    /// Die Ruecknahme nimmt den Beitrag zurueck und laesst die Buchung stehen: sie gehoert der Bank,
    /// nicht diesem Import. Genau hier waere der teure Fehler - ein Rollback, der die Bankbuchung
    /// mitnimmt, waere Datenverlust aus einer Aktion, die "rueckgaengig" heisst.
    /// </summary>
    [Fact]
    public async Task RollingBackTakesOnlyTheContributionBackNotTheTransaction()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await ImportAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "split-original", splitType: "Original", isTransfer: true),
            Row("28.08.2026", -4m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "split-a", "split-original", "Teilbuchung"),
            Row("28.08.2026", -6m, "Supermarkt", "Einkauf", "Wohnen", "Haushalt", "split-b", "split-original", "Restbetrag")));

        var jobId = await JobIdAsync(factory, scenario);
        using var rollback = await SendAsync(client, HttpMethod.Post,
            $"/api/import-jobs/{jobId:D}/rollback?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var transaction = Assert.Single(await db.Transactions.ToListAsync());
            Assert.Equal("enable-banking:existing", transaction.ExternalKey);
            Assert.False(transaction.IsTransfer);
            Assert.Null(transaction.CategoryId);
            Assert.Equal("none", transaction.CategorizationSource);
            Assert.Empty(await db.TransactionAllocations.ToListAsync());
        });
    }

    /// <summary>
    /// Was der Nutzer nach dem Import selbst entschieden hat, ueberlebt die Ruecknahme. Der Import
    /// nimmt seinen Beitrag zurueck - nicht den, der inzwischen an seiner Stelle steht.
    /// </summary>
    [Fact]
    public async Task RollingBackKeepsWhatTheUserDecidedAfterwards()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await ImportAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "enrich-1")));
        var ownCategory = await CategoriseByHandAsync(factory, scenario);
        var jobId = await JobIdAsync(factory, scenario);

        using var rollback = await SendAsync(client, HttpMethod.Post,
            $"/api/import-jobs/{jobId:D}/rollback?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var transaction = Assert.Single(await db.Transactions.ToListAsync());
            Assert.Equal(ownCategory, transaction.CategoryId);
            Assert.Equal("manual", transaction.CategorizationSource);
        });
    }

    /// <summary>
    /// Ein Import, der nur ergaenzt hat, bietet seine Ruecknahme auch an. Der Endpunkt nahm sie
    /// schon an; der Verlauf zeigte den Knopf nicht, weil er nur erzeugte Buchungen zaehlte.
    /// </summary>
    [Fact]
    public async Task AnImportThatOnlyEnrichedOffersItsRollback()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var result = await ImportAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "enrich-1")));
        Assert.Equal(0, result.TransactionsImported);
        Assert.Equal(1, result.EnrichedExistingTransactions);

        using var history = await SendAsync(client, HttpMethod.Get,
            $"/api/import-jobs?fullWorthSpaceId={scenario.Space:D}&adapterKey=finanzguru_xlsx", scenario.User);
        history.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        var job = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.True(job.GetProperty("rollbackAvailable").GetBoolean());
    }

    /// <summary>
    /// Wer den Raum nicht besitzt, darf keine Kategorie anlegen - fuer ihn ergaenzt eine unbekannte
    /// Kategorie beim Festschreiben nichts. Die Vorschau darf es ihm dann auch nicht versprechen.
    /// </summary>
    [Fact]
    public async Task AMemberIsNotPromisedAnEnrichmentWithACategoryTheyCannotCreate()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, FullWorthSpaceRoles.Member);
        using var client = factory.CreateClient();

        using var stage = await SendAsync(client, HttpMethod.Post,
            $"/api/import/finanzguru/stage?fullWorthSpaceId={scenario.Space:D}", scenario.User, Create(
                Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Gibt es nicht", "Auch nicht", "enrich-1")));
        stage.EnsureSuccessStatusCode();
        var preview = (await stage.Content.ReadFromJsonAsync<FinanzguruStagePreview>())!;
        Assert.Equal(1, preview.MatchedExisting);
        Assert.Equal(0, preview.EnrichedExisting);

        using var commit = await SendAsync(client, HttpMethod.Post,
            $"/api/import/finanzguru/jobs/{preview.JobId:D}/commit?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        commit.EnsureSuccessStatusCode();
        var result = (await commit.Content.ReadFromJsonAsync<FinanzguruImportResult>())!;
        Assert.Equal(preview.EnrichedExisting, result.EnrichedExistingTransactions);
    }

    /// <summary>
    /// Die Umbuchung traegt keine Herkunftsmarke. Wurde die Buchung nach der Ergaenzung noch einmal
    /// geaendert, kann das die Bestaetigung des Nutzers gewesen sein - dann bleibt die Kennzeichnung.
    /// </summary>
    [Fact]
    public async Task ATransferFlagOnABookingEditedSinceSurvivesTheRollback()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await ImportAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "enrich-1", isTransfer: true)));
        await factory.SeedAsync(async db =>
        {
            var transaction = await db.Transactions.SingleAsync(item => item.AccountId == scenario.Account);
            transaction.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(5);
            await db.SaveChangesAsync();
        });
        var jobId = await JobIdAsync(factory, scenario);

        using var rollback = await SendAsync(client, HttpMethod.Post,
            $"/api/import-jobs/{jobId:D}/rollback?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.True((await db.Transactions.SingleAsync(item => item.AccountId == scenario.Account)).IsTransfer));
    }

    /// <summary>Die Vorschau nennt dieselbe Zahl, die danach eintritt.</summary>
    [Fact]
    public async Task ThePreviewAnnouncesTheSameNumberOfEnrichments()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var workbook = Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "enrich-1"),
            Row("27.08.2026", -20m, "Baumarkt", "Einkauf", "Wohnen", "Haushalt", "new-1"));

        using var stage = await SendAsync(client, HttpMethod.Post,
            $"/api/import/finanzguru/stage?fullWorthSpaceId={scenario.Space:D}", scenario.User, workbook);
        Assert.Equal(HttpStatusCode.OK, stage.StatusCode);
        var preview = await stage.Content.ReadFromJsonAsync<FinanzguruStagePreview>();
        Assert.NotNull(preview);
        Assert.Equal(1, preview!.MatchedExisting);
        Assert.Equal(1, preview.EnrichedExisting);

        using var commit = await SendAsync(client, HttpMethod.Post,
            $"/api/import/finanzguru/jobs/{preview.JobId:D}/commit?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        commit.EnsureSuccessStatusCode();
        var result = await commit.Content.ReadFromJsonAsync<FinanzguruImportResult>();
        Assert.NotNull(result);
        Assert.Equal(preview.EnrichedExisting, result!.EnrichedExistingTransactions);
        Assert.Equal(preview.MatchedExisting, result.MatchedExistingTransactions);
        Assert.Equal(preview.NewRows, result.TransactionsImported);
    }

    private static async Task<FinanzguruImportResult> ImportAsync(HttpClient client, Scenario scenario, byte[] workbook)
    {
        using var response = await SendAsync(client, HttpMethod.Post,
            $"/api/import/finanzguru?fullWorthSpaceId={scenario.Space:D}", scenario.User, workbook);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<FinanzguruImportResult>();
        Assert.NotNull(result);
        return result!;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, Guid userId, byte[]? workbook = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (workbook is not null)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(workbook);
            file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            form.Add(file, "file", "finanzguru.xlsx");
            request.Content = form;
        }
        return await client.SendAsync(request);
    }

    private static async Task<Guid> JobIdAsync(BackendWebApplicationFactory factory, Scenario scenario)
    {
        var jobId = Guid.Empty;
        await factory.SeedAsync(async db =>
            jobId = await db.Database.SqlQuery<Guid>(
                $"""SELECT "Id" AS "Value" FROM "ImportJobs" WHERE "FullWorthSpaceId" = {scenario.Space}""").SingleAsync());
        return jobId;
    }

    /// <summary>Der Nutzer entscheidet selbst - das ist, was <c>"manual"</c> bedeutet.</summary>
    private static async Task<Guid> CategoriseByHandAsync(BackendWebApplicationFactory factory, Scenario scenario)
    {
        var categoryId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = scenario.Space,
                Key = $"own-{categoryId:N}",
                Name = "Meine Kategorie"
            });
            var transaction = await db.Transactions.SingleAsync(item => item.AccountId == scenario.Account);
            transaction.CategoryId = categoryId;
            transaction.CategorizationSource = "manual";
            await db.SaveChangesAsync();
        });
        return categoryId;
    }

    private sealed record Scenario(Guid Space, Guid User, Guid Account);

    private static async Task<Scenario> SeedAsync(
        BackendWebApplicationFactory factory, string role = FullWorthSpaceRoles.Owner)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.User,
                EmailNormalized = $"{scenario.User:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Import owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "Import space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.User,
                Role = role
            });

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
            // Die Buchung, die die Bank schon geliefert hat: ohne Kategorie, ohne Aufteilung,
            // ohne Umbuchungskennzeichnung - genau der Zustand, in dem ergaenzt werden darf.
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
                CategorizationSource = "none",
                RawJson = "{}"
            });

            await db.SaveChangesAsync();
        });
        return scenario;
    }
}
