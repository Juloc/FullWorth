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

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// "Einmal bestaetigte Zuordnungen werden fuer spaetere Importe wiederverwendet" (#131, Abschnitt 3).
///
/// Der Finanzguru-Weg kann das seit #112. Der allgemeine CSV/XLSX-Weg konnte es nicht: wer denselben
/// Bankexport zum zweiten Mal hochlud, ordnete jedes Quellkonto wieder von Hand zu.
/// </summary>
public sealed class RememberedSourceAccountTests
{
    private const string Csv = "Datum;Betrag;Empfänger;Konto\r\n29.08.2026;-12,34;REWE;C24 Girokonto\r\n";

    [Fact]
    public async Task TheSecondImportOfTheSameFileProposesTheAccountChosenTheFirstTime()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (owner, account) = await SeedAsync(factory);

        var first = await UploadAsync(client, owner, Csv);
        Assert.Null(await RememberedAsync(client, owner, first, "C24 Girokonto"));
        await CommitAsync(client, owner, first, new() { ["C24 Girokonto"] = account });

        // Andere Schreibweise, gleiches Konto: die Bezeichnung wird normalisiert gemerkt.
        var second = await UploadAsync(client, owner, Csv.Replace("C24 Girokonto", "c24 girokonto "));
        Assert.Equal(account, await RememberedAsync(client, owner, second, "c24 girokonto"));
    }

    /// <summary>
    /// Gemerkt wird erst beim Festschreiben. Ein Import, der nach dem Zuordnen abgebrochen wurde, soll
    /// die naechste Vorauswahl nicht praegen - die Zuordnung war bis dahin ein Entwurf.
    /// </summary>
    [Fact]
    public async Task AnUncommittedImportLeavesNoMemory()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (owner, _) = await SeedAsync(factory);

        await UploadAsync(client, owner, Csv);
        var second = await UploadAsync(client, owner, Csv);
        Assert.Null(await RememberedAsync(client, owner, second, "C24 Girokonto"));
    }

    /// <summary>
    /// Ein Konto, das der Import selbst angelegt hat, wird mit seiner echten Kennung gemerkt - nicht mit
    /// dem Platzhalter, unter dem es im Commit entstand. Sonst zeigte die Erinnerung auf nichts.
    /// </summary>
    [Fact]
    public async Task AnAccountCreatedByTheImportIsRememberedUnderItsRealId()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (owner, _) = await SeedAsync(factory);

        var first = await UploadAsync(client, owner, Csv);
        await CommitAsync(client, owner, first, [], new() { ["C24 Girokonto"] = "C24 aus Datei" });

        Guid created = Guid.Empty;
        await factory.SeedAsync(async db =>
            created = (await db.Accounts.AsNoTracking().SingleAsync(item => item.DisplayName == "C24 aus Datei")).Id);

        var second = await UploadAsync(client, owner, Csv);
        Assert.Equal(created, await RememberedAsync(client, owner, second, "C24 Girokonto"));
    }

    /// <summary>
    /// Verschwindet das Konto, verschwindet die Zuordnung darauf - und der naechste Import schlaegt
    /// wieder vor statt auf ein geloeschtes Konto zu zeigen.
    /// </summary>
    [Fact]
    public async Task ADeletedAccountIsForgotten()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var (owner, account) = await SeedAsync(factory);

        var first = await UploadAsync(client, owner, Csv);
        await CommitAsync(client, owner, first, new() { ["C24 Girokonto"] = account });

        // Erst die Buchungen, dann das Konto - so wie es der Kontoloeschpfad auch tut.
        await factory.SeedAsync(async db =>
        {
            db.Transactions.RemoveRange(db.Transactions.Where(item => item.AccountId == account));
            await db.SaveChangesAsync();
            db.AccountOwners.RemoveRange(db.AccountOwners.Where(item => item.AccountId == account));
            db.Accounts.Remove(await db.Accounts.SingleAsync(item => item.Id == account));
            await db.SaveChangesAsync();
        });

        var second = await UploadAsync(client, owner, Csv);
        Assert.Null(await RememberedAsync(client, owner, second, "C24 Girokonto"));
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid owner, string csv)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "umsaetze.csv");
        content.Add(new StringContent(JsonSerializer.Serialize(new
        {
            date = "Datum",
            amount = "Betrag",
            currency = (string?)null,
            counterparty = "Empfänger",
            description = (string?)null,
            account = "Konto",
            category = (string?)null,
            externalKey = (string?)null
        }), Encoding.UTF8, "application/json"), "mapping");
        request.Content = content;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("jobId").GetGuid();
    }

    private static async Task<Guid?> RememberedAsync(HttpClient client, Guid owner, Guid jobId, string source)
    {
        using var request = UserRequest(HttpMethod.Get,
            $"/api/import-mapping/jobs/{jobId:D}/summary?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.GetProperty("sourceAccounts").EnumerateArray()
            .Single(item => item.GetProperty("source").GetString()?.Trim() == source);
        var value = entry.GetProperty("rememberedAccountId");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetGuid();
    }

    private static async Task CommitAsync(
        HttpClient client, Guid owner, Guid jobId,
        Dictionary<string, Guid?> mappings, Dictionary<string, string>? newAccountNames = null)
    {
        using var request = UserRequest(HttpMethod.Post,
            $"/api/import-mapping/jobs/{jobId:D}/commit?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", owner);
        request.Content = JsonContent.Create(new
        {
            sourceAccountMappings = mappings,
            newAccountNames = newAccountNames ?? [],
            defaultAccountId = (Guid?)null,
            categoryMappings = new Dictionary<string, Guid?>(),
            createMissingCategories = false,
            runFullWorthCategorization = false,
            candidateIds = (Guid[]?)null
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<(Guid Owner, Guid Account)> SeedAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid();
        var account = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@LOCAL.TEST",
                DisplayName = "Owner",
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
                IdentificationHash = $"manual|{account:N}",
                ProviderAccountId = $"manual-{account:N}",
                InstitutionName = "C24",
                DisplayName = "Girokonto",
                Currency = "EUR"
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            await db.SaveChangesAsync();
        });
        return (owner, account);
    }
}
