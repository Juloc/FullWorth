using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Beim Zusammenfuehren zweier Buchungen wird eine davon geloescht. Bis hierher hing die
/// Zusammenfuehrung vier von sechzehn Beziehungen um, und der Rest richtete sich nach seiner
/// Fremdschluesselregel: Schlagworte, Pruefzustand und Vertragszuordnung fielen per CASCADE weg -
/// stillschweigend, mitten in einem automatischen Banksync -, waehrend ein Asset-Cashflow oder ein
/// Beleg-Zahlungslink auf RESTRICT stand und die Zusammenfuehrung platzen liess.
///
/// Diese Tests haetten beides gefunden.
/// </summary>
public sealed class TransactionMergeTests
{
    private sealed record Paar(Guid Winner, Guid Loser);

    /// <summary>So, wie EF eine Kennung auf SQLite ablegt: Text in Grossbuchstaben.</summary>
    private static string Text(Guid id) => id.ToString().ToUpperInvariant();

    private static async Task<Paar> ZweiBuchungenAsync(FullWorthDbContext db)
    {
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var winner = Guid.NewGuid();
        var loser = Guid.NewGuid();

        db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Space", BaseCurrency = "EUR" });
        db.Accounts.Add(new FinanceAccount
        {
            Id = accountId,
            FullWorthSpaceId = spaceId,
            Provider = "manual",
            IdentificationHash = $"manual:{accountId:N}",
            ProviderAccountId = $"manual:{accountId:N}",
            InstitutionName = "Test",
            DisplayName = "Konto",
            Currency = "EUR"
        });
        db.Transactions.AddRange(
            Buchung(winner, accountId, "bank:winner"),
            Buchung(loser, accountId, "finanzguru:loser"));
        await db.SaveChangesAsync();
        return new Paar(winner, loser);
    }

    private static FinanceTransaction Buchung(Guid id, Guid accountId, string externalKey) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = externalKey,
        BookingDate = new DateOnly(2026, 8, 20),
        ValueDate = new DateOnly(2026, 8, 20),
        Amount = -30m,
        Currency = "EUR",
        Counterparty = "Amazon",
        NormalizedCounterparty = "amazon",
        Description = "Test",
        Status = "BOOK",
        CategorizationSource = "none",
        RawJson = "{}"
    };

    /// <summary>
    /// Schlagwort, Pruefzustand und Vertragszuordnung hingen mit ON DELETE CASCADE an der Buchung -
    /// wer eine importierte Zeile mit einer Bankzeile zusammenfuehrte, verlor sie ersatzlos.
    /// </summary>
    [Fact]
    public async Task WasDerNutzerAngehaengtHatLandetBeimGewinner()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var paar = await ZweiBuchungenAsync(db);
        var tagId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "TransactionTags" ("TransactionId","TagId","CreatedAt")
VALUES ('{Text(paar.Loser)}','{Text(tagId)}','2026-08-20T00:00:00+00:00');
INSERT INTO "TransactionReviewStates" ("TransactionId","FullWorthSpaceId","IsReviewed","UpdatedAt")
VALUES ('{Text(paar.Loser)}','{Text(spaceId)}',1,'2026-08-20T00:00:00+00:00');
INSERT INTO "ContractTransactionLinks" ("Id","FullWorthSpaceId","ContractId","TransactionId","Amount","LinkSource","CreatedAt")
VALUES ('{Text(Guid.NewGuid())}','{Text(spaceId)}','{Text(contractId)}','{Text(paar.Loser)}',-30,'manual','2026-08-20T00:00:00+00:00');
""");

        await TransactionMergeService.MoveDependenciesAsync(db, paar.Loser, paar.Winner, CancellationToken.None);
        db.Transactions.Remove(await db.Transactions.SingleAsync(t => t.Id == paar.Loser));
        await db.SaveChangesAsync();

        Assert.Equal(paar.Winner, await BuchungAsync(db, "SELECT \"TransactionId\" FROM \"TransactionTags\""));
        Assert.Equal(paar.Winner, await BuchungAsync(db, "SELECT \"TransactionId\" FROM \"TransactionReviewStates\""));
        Assert.Equal(paar.Winner, await BuchungAsync(db, "SELECT \"TransactionId\" FROM \"ContractTransactionLinks\""));
    }

    /// <summary>
    /// Ein Asset-Cashflow steht auf RESTRICT: er wanderte nicht mit, also scheiterte das Loeschen der
    /// Verliererzeile an der Fremdschluesselsperre und riss die ganze Zusammenfuehrung mit.
    /// </summary>
    [Fact]
    public async Task EineRestrictBeziehungLaesstDieZusammenfuehrungNichtPlatzen()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var paar = await ZweiBuchungenAsync(db);

        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "AssetCashflowEntries"
  ("Id","FullWorthSpaceId","AssetId","TransactionId","Date","Type","Amount","Direction","Currency","IsPlanned","CreatedAt","UpdatedAt")
VALUES ('{Text(Guid.NewGuid())}','{Text(Guid.NewGuid())}','{Text(Guid.NewGuid())}','{Text(paar.Loser)}','2026-08-20','rent',-30,'out','EUR',0,
        '2026-08-20T00:00:00+00:00','2026-08-20T00:00:00+00:00');
""");

        await TransactionMergeService.MoveDependenciesAsync(db, paar.Loser, paar.Winner, CancellationToken.None);
        db.Transactions.Remove(await db.Transactions.SingleAsync(t => t.Id == paar.Loser));
        await db.SaveChangesAsync();

        Assert.Equal(paar.Winner, await BuchungAsync(db, "SELECT \"TransactionId\" FROM \"AssetCashflowEntries\""));
    }

    /// <summary>
    /// Traegt der Gewinner dasselbe Schlagwort schon, kann die Zeile des Verlierers nicht daneben -
    /// der Primaerschluessel ist (Buchung, Schlagwort). Sie faellt weg, statt das Umhaengen zu
    /// sprengen.
    /// </summary>
    [Fact]
    public async Task EinDoppeltesSchlagwortSprengtDasUmhaengenNicht()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var paar = await ZweiBuchungenAsync(db);
        var tagId = Guid.NewGuid();

        await db.Database.ExecuteSqlRawAsync($"""
INSERT INTO "TransactionTags" ("TransactionId","TagId","CreatedAt")
VALUES ('{Text(paar.Loser)}','{Text(tagId)}','2026-08-20T00:00:00+00:00'),
       ('{Text(paar.Winner)}','{Text(tagId)}','2026-08-20T00:00:00+00:00');
""");

        await TransactionMergeService.MoveDependenciesAsync(db, paar.Loser, paar.Winner, CancellationToken.None);

        var rows = await SkalarAsync(db, "SELECT COUNT(*) FROM \"TransactionTags\"");
        Assert.Equal(1L, rows);
        Assert.Equal(paar.Winner, await BuchungAsync(db, "SELECT \"TransactionId\" FROM \"TransactionTags\""));
    }

    /// <summary>
    /// Zwei eigene Aufteilungen lassen sich nicht zusammenlegen: der Gewinner haette danach doppelt
    /// so viel aufgeteilt, wie er wert ist. Frueher loeschte die Zusammenfuehrung die Aufteilung des
    /// Verlierers einfach - Handarbeit des Nutzers, ohne Rueckfrage weg.
    /// </summary>
    [Fact]
    public async Task ZweiEigeneAufteilungenWerdenNichtZusammengelegt()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var paar = await ZweiBuchungenAsync(db);

        db.TransactionAllocations.AddRange(
            new TransactionAllocation { TransactionId = paar.Loser, Amount = -30m },
            new TransactionAllocation { TransactionId = paar.Winner, Amount = -30m });
        await db.SaveChangesAsync();

        Assert.False(await TransactionMergeService.CanMergeAsync(
            db, paar.Loser, paar.Winner, CancellationToken.None));
    }

    /// <summary>Nur der Verlierer teilt auf - dann darf die Aufteilung mitwandern.</summary>
    [Fact]
    public async Task EineEinzelneAufteilungWandertMit()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var paar = await ZweiBuchungenAsync(db);

        db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = paar.Loser, Amount = -30m });
        await db.SaveChangesAsync();

        Assert.True(await TransactionMergeService.CanMergeAsync(
            db, paar.Loser, paar.Winner, CancellationToken.None));

        await TransactionMergeService.MoveDependenciesAsync(db, paar.Loser, paar.Winner, CancellationToken.None);
        db.Transactions.Remove(await db.Transactions.SingleAsync(t => t.Id == paar.Loser));
        await db.SaveChangesAsync();

        var allocation = await db.TransactionAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(paar.Winner, allocation.TransactionId);
    }

    private static async Task<object?> SkalarAsync(FullWorthDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>SQLite gibt eine Kennung je nach Schreibweg als Text oder als Blob zurueck.</summary>
    private static async Task<Guid> BuchungAsync(FullWorthDbContext db, string sql) =>
        await SkalarAsync(db, sql) switch
        {
            Guid guid => guid,
            string text => Guid.Parse(text),
            byte[] bytes => new Guid(bytes),
            var other => throw new InvalidOperationException($"Unerwartet: {other?.GetType().Name ?? "null"}")
        };
}
