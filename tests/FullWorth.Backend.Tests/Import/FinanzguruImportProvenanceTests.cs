using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Der Finanzguru-Import laeuft ueber denselben Auftrag wie jeder andere Dateiimport (#131).
///
/// Er war der einzige ohne einen: er schrieb Buchungen direkt in die Tabelle, ohne
/// Herkunftsverknuepfung. Sichtbar war er dadurch in keiner Importliste, und rueckgaengig machen
/// liess er sich gar nicht - die einzige Spur war ein Praefix im ExternalKey, das jede spaetere
/// Aenderung ueberschreiben darf.
///
/// Die Konten bleiben, wie sie sind. Sie umzubauen ist Teil (c) und braucht eine eigene Migration mit
/// eigenem Rueckweg: <c>LinkExplicitAsync</c> verschiebt gebuchte Umsaetze zwischen Konten.
/// </summary>
public sealed class FinanzguruImportProvenanceTests
{
    [Fact]
    public async Task AnImportLeavesAJobAndALinkForEveryTransactionItCreated()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedAsync(factory);

        int imported;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinanzguruImportService>();
            var result = await service.ImportRowsAsync(userId, spaceId, Rows(), CancellationToken.None);
            Assert.NotNull(result);
            imported = result!.TransactionsImported;
            Assert.Equal(2, imported);
        }

        await factory.SeedAsync(async db =>
        {
            var jobs = await db.Database.SqlQuery<ImportJobProbe>(
                $"""SELECT "Id", "AdapterKey", "Status", "ImportedCount" FROM "ImportJobs" WHERE "FullWorthSpaceId" = {spaceId}""")
                .ToListAsync();
            var job = Assert.Single(jobs);
            Assert.Equal("finanzguru_xlsx", job.AdapterKey);
            Assert.Equal("completed", job.Status);
            Assert.Equal(imported, job.ImportedCount);

            var links = await db.Database.SqlQuery<int>(
                $"""SELECT count(*)::int AS "Value" FROM "ImportTransactionLinks" WHERE "ImportJobId" = {job.Id}""")
                .SingleAsync();
            Assert.Equal(imported, links);
        });
    }

    [Fact]
    public async Task ImportingTheSameRowsAgainAddsNoSecondSetOfLinks()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedAsync(factory);

        for (var run = 0; run < 2; run++)
        {
            using var scope = factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<FinanzguruImportService>();
            Assert.NotNull(await service.ImportRowsAsync(userId, spaceId, Rows(), CancellationToken.None));
        }

        await factory.SeedAsync(async db =>
        {
            // Zwei Auftraege - es gab zwei Importe - aber nur die zwei Buchungen des ersten sind
            // verknuepft. Der zweite hat nichts angelegt, also kann er auch nichts zurueckzunehmen haben.
            var jobs = await db.Database.SqlQuery<int>(
                $"""SELECT count(*)::int AS "Value" FROM "ImportJobs" WHERE "FullWorthSpaceId" = {spaceId}""").SingleAsync();
            Assert.Equal(2, jobs);

            var links = await db.Database.SqlQuery<int>(
                $"""SELECT count(*)::int AS "Value" FROM "ImportTransactionLinks" l JOIN "ImportJobs" j ON j."Id" = l."ImportJobId" WHERE j."FullWorthSpaceId" = {spaceId}""").SingleAsync();
            Assert.Equal(2, links);
        });
    }

    private sealed record ImportJobProbe(Guid Id, string AdapterKey, string Status, int ImportedCount);

    private static List<FinanzguruRow> Rows() =>
    [
        Row("fg-1", new DateOnly(2026, 8, 3), -24.90m, "Kaufland"),
        Row("fg-2", new DateOnly(2026, 8, 5), 2100m, "AERA GmbH")
    ];

    private static FinanzguruRow Row(string bookingId, DateOnly date, decimal amount, string counterparty) =>
        new(
            RowNumber: 0,
            BookingDate: date,
            ReferenceAccount: "DE02120300000000202051",
            ReferenceAccountName: "Girokonto",
            Amount: amount,
            Currency: "EUR",
            Counterparty: counterparty,
            CounterpartyIban: null,
            Description: counterparty,
            EntryReference: null,
            MainCategory: null,
            SubCategory: null,
            IsTransfer: false,
            BookingId: bookingId,
            OriginalReferenceId: null,
            SplitType: null,
            RawValues: new Dictionary<string, string?> { ["Betrag"] = amount.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static async Task<(Guid UserId, Guid SpaceId)> SeedAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorth.Backend.Modules.Users.FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@LOCAL.TEST",
                DisplayName = "Test",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpace
            {
                Id = spaceId,
                Name = "Test",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorth.Backend.Modules.FullWorthSpaces.FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });
        return (userId, spaceId);
    }
}
