using System.IO.Compression;
using System.Net;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Die Etiketten im CSV-Export.
///
/// <c>ExportPortabilityRegressionTests</c> prueft, dass <c>tags.csv</c> und
/// <c>transaction_tags.csv</c> in der ZIP-Datei liegen - aber es legt gar keine Etiketten an, also
/// waren beide dort immer leer. Der Inhalt war damit von nichts abgedeckt.
///
/// Das faellt auf, seit die Abfrage einen Namen hat: sie fragte je Buchung einzeln nach den
/// Etiketten und tut es jetzt in einem Zug fuer alle. Dieser Test deckt beides ab - dass die
/// Zuordnung stimmt, und dass die Buchung eines fremden Kontos auch mit Etikett nicht im Export
/// auftaucht.
/// </summary>
public sealed class CsvExportTagsTests
{
    [Fact]
    public async Task Tags_are_exported_for_visible_transactions_only()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        var eigenesKonto = Guid.NewGuid();
        var fremdesKonto = Guid.NewGuid();
        var eigeneBuchung = Guid.NewGuid();
        var fremdeBuchung = Guid.NewGuid();
        var etikett = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Exporteur",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });

            foreach (var (accountId, name) in new[] { (eigenesKonto, "MEIN_KONTO"), (fremdesKonto, "FREMDES_KONTO") })
                db.Accounts.Add(new FinanceAccount
                {
                    Id = accountId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Provider = "manual",
                    ProviderAccountId = $"manual-{accountId:N}",
                    IdentificationHash = $"manual-{accountId:N}",
                    InstitutionName = "Manual",
                    DisplayName = name,
                    Currency = "EUR",
                    IsActive = true
                });

            // Nur das eine Konto gehoert ihm - das andere liegt im selben Space, aber ohne Eigentum.
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = eigenesKonto,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });

            db.Transactions.Add(Buchung(eigeneBuchung, eigenesKonto, "MEINE_BUCHUNG"));
            db.Transactions.Add(Buchung(fremdeBuchung, fremdesKonto, "FREMDE_BUCHUNG"));

            db.FinanceTags.Add(new FinanceTag
            {
                Id = etikett,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "URLAUB_ETIKETT",
                NormalizedName = "urlaub_etikett",
                Color = "#112233"
            });
            await db.SaveChangesAsync();

            // Beide Buchungen bekommen dasselbe Etikett.
            foreach (var transactionId in new[] { eigeneBuchung, fremdeBuchung })
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "TransactionTags" ("TransactionId","TagId","CreatedAt")
VALUES ({transactionId},{etikett},{DateTimeOffset.UtcNow})
""");

            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FinanceCapabilityGrants" ("FullWorthSpaceId","UserId","Capability","IsAllowed","UpdatedAt")
VALUES ({FullWorthSpaceDefaults.LegacyId},{userId},{"export.read"},{true},{DateTimeOffset.UtcNow})
""");
        });

        using var request = UserRequest(
            $"/api/export/csv-zip?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&includePurchases=false&includeInvestments=false",
            userId);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var tags = await ReadAsync(archive, "tags.csv");
        var transactionTags = await ReadAsync(archive, "transaction_tags.csv");

        Assert.Contains("URLAUB_ETIKETT", tags, StringComparison.Ordinal);
        Assert.Contains(etikett.ToString(), transactionTags, StringComparison.Ordinal);
        Assert.Contains(eigeneBuchung.ToString(), transactionTags, StringComparison.Ordinal);
        Assert.DoesNotContain(fremdeBuchung.ToString(), transactionTags, StringComparison.Ordinal);
    }

    private static FinanceTransaction Buchung(Guid id, Guid accountId, string counterparty) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = $"{id:N}",
        Status = "BOOK",
        BookingDate = new DateOnly(2026, 3, 1),
        Amount = -19.99m,
        Currency = "EUR",
        Counterparty = counterparty
    };

    private static async Task<string> ReadAsync(ZipArchive archive, string name)
    {
        var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == name);
        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync();
    }

    private static HttpRequestMessage UserRequest(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
