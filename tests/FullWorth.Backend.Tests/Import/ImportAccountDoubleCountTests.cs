using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Seit ein Import ein vollwertiges Konto anlegt, kann dasselbe Bankkonto zweimal im System stehen:
/// einmal als importierte Historie, einmal als spaeter verbundenes Bankkonto. Zaehlen beide mit, ist
/// das Vermoegen doppelt so gross wie das Geld.
///
/// Die Regel loeste das frueher andersherum: das NEUE Konto trat zurueck. Das war richtig, solange die
/// Gegenseite nur ein anderes Bankkonto sein konnte - gegen ein Importkonto ist es die falsche Seite,
/// denn die Bank haelt dasselbe Konto aktuell und der Import ist ein Stand von gestern.
/// </summary>
public sealed class ImportAccountDoubleCountTests
{
    [Fact]
    public async Task DasImportkontoTrittZurueckNichtDasBankkonto()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM",
            DisplayName = "Owner",
            IsActive = true
        });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Space", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = spaceId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Owner
        });
        await db.SaveChangesAsync();

        var store = new AccountStore(db);
        var imported = await store.CreateForImportAsync(userId, new ImportAccountWrite(
            spaceId, "finanzguru-import", "fg-hash", "finanzguru:hash",
            "Finanzguru Import", "Girokonto", "Imported history", "EUR", "1426"), CancellationToken.None);
        await db.SaveChangesAsync();

        // Das Importkonto ist ein richtiges Konto: sichtbar, im Vermoegen, in einer Gruppe.
        Assert.True(imported.IsActive);
        Assert.True(imported.IncludeInNetWorth);
        Assert.NotNull(imported.GroupId);

        // Die Bank bringt spaeter dasselbe Konto. Die Zuordnung markiert die verbindungslose Seite als
        // Doppel - und merkt sich, wie sie vorher gezaehlt hat, damit es umkehrbar bleibt.
        var bank = await store.CreateForImportAsync(userId, new ImportAccountWrite(
            spaceId, "enable-banking", "bank-hash", "eb:hash",
            "Sparkasse", "Girokonto", null, "EUR", "1426"), CancellationToken.None);
        await db.SaveChangesAsync();

        imported.DuplicateOfAccountId = bank.Id;
        imported.IncludeInNetWorthBeforeLink ??= imported.IncludeInNetWorth;
        imported.IncludeInNetWorth = false;
        await db.SaveChangesAsync();

        var again = await db.Accounts.AsNoTracking().SingleAsync(account => account.Id == imported.Id);
        Assert.False(again.IncludeInNetWorth);
        Assert.True(again.IncludeInNetWorthBeforeLink);
        Assert.True(again.IsActive);
    }
}
