using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FullWorth.Backend.Tests.Import.FinanzguruWorkbook;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Ein Quellkonto ohne Gegenstueck bekommt sein Ziel schon in der Vorschau (#131, Abschnitt 4).
///
/// Bisher legte der erste Import dafuer ein eigenes Importkonto an, und erst eine zweite Handlung -
/// verknuepfen, Kontostand bestaetigen, Treffer pruefen - brachte die Buchungen an das echte Konto.
/// Jetzt kann der Nutzer das Ziel waehlen, bevor irgendetwas geschrieben ist. Die Grenze: nur dort, wo
/// dabei nichts doppelt gezaehlt werden kann.
/// </summary>
public sealed class FinanzguruTargetChoiceTests
{
    // Eine IBAN, die zu keinem Konto passt - die Quelle hat also kein Gegenstueck.
    private const string UnknownIban = "DE89370400440532019999";

    [Fact]
    public async Task AChosenTargetTakesTheRowsWithoutAnImportAccount()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var preview = await StageAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "t-1", reference: UnknownIban)));
        var source = Assert.Single(preview.Accounts);
        Assert.Equal("new", source.Status);
        Assert.True(source.Retargetable);

        var retargeted = await RetargetAsync(client, scenario, preview.JobId, new() { [source.SourceKey] = scenario.Target });
        var linked = Assert.Single(retargeted.Accounts);
        Assert.Equal("linked", linked.Status);
        Assert.Equal(scenario.Target, linked.AccountId);

        var result = await CommitAsync(client, scenario, preview.JobId, new() { [source.SourceKey] = scenario.Target });
        Assert.Equal(1, result.TransactionsImported);
        Assert.Equal(0, result.AccountsCreated);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Transactions.CountAsync(item => item.AccountId == scenario.Target));
            // Kein Importkonto dazwischen - genau das, was Abschnitt 4 verlangt.
            Assert.False(await db.Accounts.AnyAsync(item => item.Provider == "finanzguru-import"));
        });
    }

    /// <summary>Beim naechsten Import derselben Quelle steht das Ziel schon da.</summary>
    [Fact]
    public async Task TheNextImportOfTheSameSourceIsAlreadyLinked()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var first = await StageAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "t-1", reference: UnknownIban)));
        var key = Assert.Single(first.Accounts).SourceKey;
        await CommitAsync(client, scenario, first.JobId, new() { [key] = scenario.Target });

        var second = await StageAsync(client, scenario, Create(
            Row("29.08.2026", -5m, "Baeckerei", "Einkauf", "Lebensmittel", "Essen", "t-2", reference: UnknownIban)));
        var again = Assert.Single(second.Accounts);
        Assert.Equal("linked", again.Status);
        Assert.Equal(scenario.Target, again.AccountId);

        var result = await CommitAsync(client, scenario, second.JobId, null);
        Assert.Equal(0, result.AccountsCreated);
        await factory.SeedAsync(async db =>
            Assert.Equal(2, await db.Transactions.CountAsync(item => item.AccountId == scenario.Target)));
    }

    /// <summary>
    /// Fuehrt ein Importkonto die Quelle schon, liegen dort Buchungen. Sie umzuleiten legte dieselben
    /// Buchungen ein zweites Mal ab - dafuer gibt es die Verknuepfung, die vorher abgleicht.
    /// </summary>
    [Fact]
    public async Task ASourceWithAnImportAccountCannotBeRedirected()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var workbook = Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "t-1", reference: UnknownIban));
        var first = await StageAsync(client, scenario, workbook);
        await CommitAsync(client, scenario, first.JobId, null);   // legt das Importkonto an

        // Die Buchungen weg, das Importkonto bleibt. So prueft dieser Test die Regel selbst und nicht
        // nebenbei die Doppelzaehlungs-Sperre, die sonst an den Buchungen im Importkonto zuerst
        // anschluege - ohne diesen Schritt blieb er gruen, auch wenn die Regel fehlte.
        await factory.SeedAsync(async db =>
        {
            db.Transactions.RemoveRange(db.Transactions.Where(item =>
                db.Accounts.Any(account => account.Id == item.AccountId && account.Provider == "finanzguru-import")));
            await db.SaveChangesAsync();
        });

        var second = await StageAsync(client, scenario, workbook);
        var source = Assert.Single(second.Accounts);
        Assert.False(source.Retargetable);

        using var response = await SendRetargetAsync(client, scenario, second.JobId, new() { [source.SourceKey] = scenario.Target });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>
    /// Liegt eine Buchung dieser Quelle schon in einem anderen Konto, stuende sie nach dem Import
    /// zweimal im Vermoegen. Die Wahl wird abgelehnt - beim Neurechnen und beim Uebernehmen.
    /// </summary>
    [Fact]
    public async Task ATargetThatWouldCountABookingTwiceIsRejected()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction
            {
                AccountId = scenario.Other,
                ExternalKey = "finanzguru:t-1",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 8, 28),
                ValueDate = new DateOnly(2026, 8, 28),
                Amount = -10m,
                Currency = "EUR",
                Counterparty = "Supermarkt",
                NormalizedCounterparty = "SUPERMARKT",
                CategorizationSource = "none",
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        });

        var preview = await StageAsync(client, scenario, Create(
            Row("28.08.2026", -10m, "Supermarkt", "Einkauf", "Lebensmittel", "Essen", "t-1", reference: UnknownIban)));
        var key = Assert.Single(preview.Accounts).SourceKey;

        using var retarget = await SendRetargetAsync(client, scenario, preview.JobId, new() { [key] = scenario.Target });
        Assert.Equal(HttpStatusCode.Conflict, retarget.StatusCode);

        using var commit = await SendCommitAsync(client, scenario, preview.JobId, new() { [key] = scenario.Target });
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);

        await factory.SeedAsync(async db =>
            Assert.Equal(0, await db.Transactions.CountAsync(item => item.AccountId == scenario.Target)));
    }

    private static async Task<FinanzguruStagePreview> StageAsync(HttpClient client, Scenario scenario, byte[] workbook)
    {
        var request = Authenticated(HttpMethod.Post, $"/api/import/finanzguru/stage?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(workbook);
        file.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "finanzguru.xlsx");
        request.Content = form;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FinanzguruStagePreview>())!;
    }

    private static async Task<FinanzguruStagePreview> RetargetAsync(
        HttpClient client, Scenario scenario, Guid jobId, Dictionary<string, Guid> targets)
    {
        using var response = await SendRetargetAsync(client, scenario, jobId, targets);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FinanzguruStagePreview>())!;
    }

    private static Task<HttpResponseMessage> SendRetargetAsync(
        HttpClient client, Scenario scenario, Guid jobId, Dictionary<string, Guid> targets)
    {
        var request = Authenticated(HttpMethod.Post,
            $"/api/import/finanzguru/jobs/{jobId:D}/targets?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        request.Content = JsonContent.Create(new { accountTargets = targets });
        return client.SendAsync(request);
    }

    private static async Task<FinanzguruImportResult> CommitAsync(
        HttpClient client, Scenario scenario, Guid jobId, Dictionary<string, Guid>? targets)
    {
        using var response = await SendCommitAsync(client, scenario, jobId, targets);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FinanzguruImportResult>())!;
    }

    private static Task<HttpResponseMessage> SendCommitAsync(
        HttpClient client, Scenario scenario, Guid jobId, Dictionary<string, Guid>? targets)
    {
        var request = Authenticated(HttpMethod.Post,
            $"/api/import/finanzguru/jobs/{jobId:D}/commit?fullWorthSpaceId={scenario.Space:D}", scenario.User);
        request.Content = JsonContent.Create(new { candidateIds = (Guid[]?)null, accountTargets = targets });
        return client.SendAsync(request);
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string url, Guid userId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Scenario(Guid Space, Guid User, Guid Target, Guid Other);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
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
                Role = FullWorthSpaceRoles.Owner
            });
            foreach (var (id, name) in new[] { (scenario.Target, "Tagesgeld"), (scenario.Other, "Anderes Konto") })
            {
                db.Accounts.Add(new FinanceAccount
                {
                    Id = id,
                    FullWorthSpaceId = scenario.Space,
                    Provider = "manual",
                    IdentificationHash = $"manual|{id:N}",
                    ProviderAccountId = $"manual-{id:N}",
                    InstitutionName = "Bank",
                    DisplayName = name,
                    Currency = "EUR"
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = id,
                    UserId = scenario.User,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
            }
            await db.SaveChangesAsync();
        });
        return scenario;
    }
}
