using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Beim Zuordnen entscheidet der Nutzer, nicht der Automatismus: er sieht vorher, welche Buchungen
/// zusammenfallen, waehlt welche Fassung gewinnt, und kann einzelne Treffer ausnehmen.
///
/// Vorher war "Verbinden" ein Knopf, nach dem Buchungen verschwunden waren, ohne dass jemand vorher
/// sagen konnte, welche - und die Bankzeile gewann immer.
/// </summary>
public sealed class FinanzguruLinkChoiceTests
{
    private sealed record Szenario(Guid User, Guid Space, Guid Import, Guid Target, Guid ImportRow, Guid TargetRow, Guid Category);

    [Fact]
    public async Task DieVorschauNenntDieTrefferBevorEtwasPassiert()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var s = await SeedAsync(db);

        var preview = await Service(db).PreviewLinkAsync(s.User, s.Space, s.Import, s.Target, CancellationToken.None);

        Assert.NotNull(preview);
        var match = Assert.Single(preview!.Matches);
        Assert.Equal(s.ImportRow, match.ImportTransactionId);
        Assert.Equal(s.TargetRow, match.TargetTransactionId);
        Assert.Equal(-30m, match.Amount);
        Assert.Equal("Shopping", match.ImportCategoryName);
        Assert.Equal(1, preview.MovedWithoutMatch);

        // Eine Vorschau schreibt nichts.
        Assert.Equal(2, await db.Transactions.AsNoTracking().CountAsync(t => t.AccountId == s.Import));
    }

    [Fact]
    public async Task DieImportierteFassungKannGewinnenOhneDieBankzeileZuLoeschen()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var s = await SeedAsync(db);

        await Service(db).LinkExplicitAsync(
            s.User, s.Space, s.Import, s.Target, null, null, CancellationToken.None,
            preferImport: true);

        // Die Bankzeile lebt: sie traegt den Schluessel, an dem die Bank sie wiedererkennt. Waere sie
        // geloescht, lieferte der naechste Abruf sie neu - das Doppel waere zurueck.
        var survivor = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == s.TargetRow);
        Assert.Equal("enable-banking:live", survivor.ExternalKey);
        // Aber der Inhalt ist der importierte.
        Assert.Equal(s.Category, survivor.CategoryId);
        Assert.False(await db.Transactions.AsNoTracking().AnyAsync(t => t.Id == s.ImportRow));
    }

    [Fact]
    public async Task EinAbgewaehlterTrefferBleibtEineEigeneBuchung()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var s = await SeedAsync(db);

        var result = await Service(db).LinkExplicitAsync(
            s.User, s.Space, s.Import, s.Target, null, null, CancellationToken.None,
            excludedImportTransactionIds: new HashSet<Guid> { s.ImportRow });

        Assert.NotNull(result);
        Assert.Equal(0, result!.TransactionsMerged);
        // Beide Zeilen stehen jetzt auf dem Zielkonto, nebeneinander.
        var stillThere = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == s.ImportRow);
        Assert.Equal(s.Target, stillThere.AccountId);
        Assert.True(await db.Transactions.AsNoTracking().AnyAsync(t => t.Id == s.TargetRow));
    }

    /// <summary>
    /// Bank und Export nennen oft verschiedene Tage fuer denselben Vorgang - die eine das Buchungs-,
    /// die andere das Wertstellungsdatum. Ein Tag Unterschied machte daraus zweimal dasselbe Geld.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 0)]   // ein Tag daneben: dieselbe Buchung
    [InlineData(3, 1, 0)]   // Rand des Fensters: noch dieselbe
    [InlineData(4, 0, 1)]   // darueber hinaus wuerde man raten: zwei Buchungen
    public async Task EinPaarTageDanebenIstTrotzdemDieselbeBuchung(int versatz, int erwarteteTreffer, int erwarteteUmzuege)
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var s = await SeedAsync(db, targetDate: new DateOnly(2026, 8, 20).AddDays(versatz), withExtraRow: false);

        var result = await Service(db).LinkExplicitAsync(
            s.User, s.Space, s.Import, s.Target, null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(erwarteteTreffer, result!.TransactionsMerged);
        Assert.Equal(erwarteteUmzuege, result.TransactionsMoved);
    }

    /// <summary>Eine genaue Uebereinstimmung darf nie gegen eine ungefaehre verlieren.</summary>
    [Fact]
    public async Task DerGenaueTrefferGewinntGegenDenUngefaehren()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var s = await SeedAsync(db, withExtraRow: false);

        var nah = Guid.NewGuid();
        db.Transactions.Add(Buchung(nah, s.Target, "enable-banking:nah", new DateOnly(2026, 8, 21), null));
        await db.SaveChangesAsync();

        await Service(db).LinkExplicitAsync(s.User, s.Space, s.Import, s.Target, null, null, CancellationToken.None);

        // Die taggleiche Zeile hat den Treffer bekommen, die tagversetzte steht unberuehrt daneben.
        Assert.False(await db.Transactions.AsNoTracking().AnyAsync(t => t.Id == s.ImportRow));
        Assert.True(await db.Transactions.AsNoTracking().AnyAsync(t => t.Id == nah));
        Assert.True(await db.Transactions.AsNoTracking().AnyAsync(t => t.Id == s.TargetRow));
    }

    /// <summary>Ohne Kontostand geht das Zuordnen trotzdem - das Feld ist optional.</summary>
    [Fact]
    public async Task OhneKontostandLaesstSichTrotzdemZuordnen()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var s = await SeedAsync(db);

        var result = await Service(db).LinkExplicitAsync(
            s.User, s.Space, s.Import, s.Target, null, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result!.CurrentBalanceAdded);
        Assert.False(await db.BalanceSnapshots.AsNoTracking().AnyAsync(b => b.AccountId == s.Target));
    }

    private static FinanzguruAccountReconciliationService Service(FullWorthDbContext db) =>
        new(db, new AuditService(db));

    private static async Task<Szenario> SeedAsync(
        FullWorthDbContext db, DateOnly? targetDate = null, bool withExtraRow = true)
    {
        var s = new Szenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        db.Users.Add(new FullWorthUser
        {
            Id = s.User,
            EmailNormalized = $"{s.User:N}@EXAMPLE.COM",
            DisplayName = "Owner",
            IsActive = true
        });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Space", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = s.Space,
            UserId = s.User,
            Role = FullWorthSpaceRoles.Owner
        });
        db.Categories.Add(new FinanceCategory
        {
            Id = s.Category,
            FullWorthSpaceId = s.Space,
            Key = "finanzguru-shopping",
            Name = "Shopping"
        });
        db.Accounts.AddRange(
            Konto(s.Import, s.Space, FinanzguruAccountReconciliationService.ImportProvider),
            Konto(s.Target, s.Space, "enable-banking"));
        db.AccountOwners.AddRange(
            new AccountOwner { AccountId = s.Import, UserId = s.User, OwnershipType = AccountOwnershipTypes.Owner },
            new AccountOwner { AccountId = s.Target, UserId = s.User, OwnershipType = AccountOwnershipTypes.Owner });

        // Ein Paar, das dasselbe meint - und optional eine Zeile, die es nur im Import gibt.
        db.Transactions.AddRange(
            Buchung(s.ImportRow, s.Import, "finanzguru:dup", new DateOnly(2026, 8, 20), s.Category),
            Buchung(s.TargetRow, s.Target, "enable-banking:live", targetDate ?? new DateOnly(2026, 8, 20), null));
        if (withExtraRow)
            db.Transactions.Add(Buchung(Guid.NewGuid(), s.Import, "finanzguru:only", new DateOnly(2026, 7, 1), null));
        await db.SaveChangesAsync();
        return s;
    }

    private static FinanceAccount Konto(Guid id, Guid space, string provider) => new()
    {
        Id = id,
        FullWorthSpaceId = space,
        Provider = provider,
        IdentificationHash = $"{provider}|{id:N}",
        ProviderAccountId = $"{provider}:{id:N}",
        InstitutionName = provider,
        DisplayName = "Konto",
        Currency = "EUR"
    };

    private static FinanceTransaction Buchung(Guid id, Guid account, string key, DateOnly date, Guid? category) => new()
    {
        Id = id,
        AccountId = account,
        ExternalKey = key,
        BookingDate = date,
        ValueDate = date,
        Amount = -30m,
        Currency = "EUR",
        Counterparty = "Amazon",
        NormalizedCounterparty = MerchantNormalization.Normalize("Amazon"),
        Description = "Test",
        Status = "BOOK",
        CategoryId = category,
        CategorizationSource = category.HasValue ? "finanzguru" : "none",
        RawJson = "{}"
    };
}
