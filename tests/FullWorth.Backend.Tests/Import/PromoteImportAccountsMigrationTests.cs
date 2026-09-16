using FullWorth.Backend.Migrations;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Die Migration, die Bestands-Importkonten zu richtigen Konten macht. Sie laeuft einmal ueber die
/// Daten echter Nutzer, und ihre vier Ausnahmen sind der ganze Punkt: was schon zugeordnet ist, was
/// dasselbe Bankkonto doppelt waere, was leer ist, und was der Nutzer laengst selbst sichtbar gemacht
/// hat.
///
/// Getestet wird die Konstante aus der Migration selbst - eine Abschrift haette den Fehler, den der
/// Test finden soll, nur mitkopiert.
/// </summary>
public sealed class PromoteImportAccountsMigrationTests
{
    [Fact]
    public async Task StuftNurDieRichtigenKontenHochUndZaehltKeinGeldDoppelt()
    {
        using var factory = new BackendWebApplicationFactory();
        var space = Guid.NewGuid();
        var user = Guid.NewGuid();
        var mitBuchungen = Guid.NewGuid();
        var leer = Guid.NewGuid();
        var zugeordnet = Guid.NewGuid();
        var doppelt = Guid.NewGuid();
        var bank = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });

            // Jeder Bereich hat seit #125 eine Standardgruppe; ohne sie haette die Migration keine,
            // in die sie ein gruppenloses Importkonto stecken koennte.
            db.AccountGroups.Add(new AccountGroup
            {
                FullWorthSpaceId = space,
                Name = "Allgemein",
                IsDefault = true
            });

            // Das echte Bankkonto, gegen das die IBAN-Dublette erkannt wird.
            db.Accounts.Add(Konto(bank, space, "enable-banking", isActive: true, counted: true, iban: "de00bank"));
            // Vier Importkonten im alten Zustand: archiviert, ausserhalb des Vermoegens.
            db.Accounts.Add(Konto(mitBuchungen, space, "finanzguru-import", false, false, null));
            db.Accounts.Add(Konto(leer, space, "finanzguru-import", false, false, null));
            db.Accounts.Add(Konto(zugeordnet, space, "finanzguru-import", false, false, null));
            db.Accounts.Add(Konto(doppelt, space, "finanzguru-import", false, false, "de00bank"));
            await db.SaveChangesAsync();

            var linked = await db.Accounts.SingleAsync(account => account.Id == zugeordnet);
            linked.ImportLinkedAccountId = bank;

            foreach (var id in new[] { mitBuchungen, zugeordnet, doppelt })
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = id,
                    ExternalKey = $"finanzguru:{Guid.NewGuid():N}",
                    BookingDate = new DateOnly(2026, 8, 20),
                    ValueDate = new DateOnly(2026, 8, 20),
                    Amount = -10m,
                    Currency = "EUR",
                    Status = "BOOK",
                    CategorizationSource = "none",
                    RawJson = "{}"
                });
            await db.SaveChangesAsync();
        });

        // Zweimal ausfuehren: eine Migration, die beim zweiten Lauf etwas anderes tut, ist eine, die
        // beim ersten etwas uebersehen hat.
        await factory.SeedAsync(db => db.Database.ExecuteSqlRawAsync(PromoteImportAccounts.PromoteSql));
        await factory.SeedAsync(db => db.Database.ExecuteSqlRawAsync(PromoteImportAccounts.PromoteSql));

        await factory.SeedAsync(async db =>
        {
            var konten = await db.Accounts.AsNoTracking()
                .Where(account => account.FullWorthSpaceId == space)
                .ToDictionaryAsync(account => account.Id);

            // Hochgestuft: hat Buchungen, ist keinem Konto zugeordnet, ist kein Doppel.
            Assert.True(konten[mitBuchungen].IsActive);
            Assert.True(konten[mitBuchungen].IncludeInNetWorth);
            Assert.NotNull(konten[mitBuchungen].GroupId);

            // Leer: eine Zeile ueber nichts waere keine Verbesserung.
            Assert.False(konten[leer].IsActive);
            Assert.False(konten[leer].IncludeInNetWorth);

            // Schon zugeordnet: ausgeraeumt, bleibt stillgelegt.
            Assert.False(konten[zugeordnet].IsActive);
            Assert.False(konten[zugeordnet].IncludeInNetWorth);

            // Gleiche IBAN wie ein aktives Bankkonto: als Doppel markiert statt hochgestuft - sonst
            // waere die Migration selbst die Doppelzaehl-Quelle.
            Assert.Equal(bank, konten[doppelt].DuplicateOfAccountId);
            Assert.False(konten[doppelt].IncludeInNetWorth);
            Assert.False(konten[bank].IncludeInNetWorth == false);

            // Kein Konto hat einen Kontostand bekommen - die Migration bewegt kein Geld.
            Assert.False(await db.BalanceSnapshots.AnyAsync(balance =>
                konten.Keys.Contains(balance.AccountId)));
        });
    }

    private static FinanceAccount Konto(
        Guid id, Guid space, string provider, bool isActive, bool counted, string? iban) => new()
    {
        Id = id,
        FullWorthSpaceId = space,
        Provider = provider,
        IdentificationHash = $"{provider}|{id:N}",
        ProviderAccountId = $"{provider}:{id:N}",
        InstitutionName = provider,
        DisplayName = $"Konto {id:N}"[..12],
        Currency = "EUR",
        IsActive = isActive,
        IncludeInNetWorth = counted,
        IbanLookup = iban
    };
}
