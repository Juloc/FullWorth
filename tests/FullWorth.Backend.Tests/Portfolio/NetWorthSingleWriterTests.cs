using Microsoft.Extensions.DependencyInjection;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Die Vermoegenshistorie hat genau EINEN Schreiber (#123).
///
/// Im Protokoll stand:
///
/// <code>
/// ERROR: duplicate key value violates unique constraint
///        "IX_NetWorthSnapshots_FullWorthSpaceId_UserId_Date_Currency"
/// DETAIL: Key (...,"Date","Currency")=(..., 2026-09-15, EUR) already exists.
/// </code>
///
/// <c>RebuildHistoryForUserAsync</c> liest die vorhandenen Snapshots, ergaenzt die fehlenden und
/// speichert. Zwei solche Laeufe nebeneinander sehen beide "heute fehlt" und legen beide an.
///
/// Der <c>FinancialDataConsistencyCoordinator</c> ist ein Singleton mit einem Semaphor und
/// serialisiert seine Laeufe. Zwei Handler in <c>FinanzguruImportEndpoints</c> riefen den Neuaufbau
/// aber DIREKT auf - am Gatter vorbei, und dazu noch ueberfluessig: das Verknuepfen committet
/// vermoegenswirksame Daten, der Interceptor hatte den Koordinator also laengst laufen lassen.
///
/// Dass der Commit allein genuegt, laesst sich hier NICHT pruefen: der Testfaktory ersetzt die
/// DbContext-Registrierung und laesst dabei die Interceptors weg, an denen der Koordinator haengt -
/// er laeuft in keinem einzigen Test. Nachgemessen wurde es mit verdrahteten Interceptors, und dann
/// werden sechs Portfolio-Tests rot, weil sie gegen das Verhalten OHNE Koordinator geschrieben sind.
/// Das ist eigene Arbeit und hat ein eigenes Issue.
///
/// Was hier bleibt, ist die Eigenschaft, an der alles haengt: der Neuaufbau ist wiederholbar. Waere
/// er es, koennte es beliebig viele Schreiber geben; da er es nur innerhalb eines Laufes ist, darf es
/// genau einen geben.
/// </summary>
public sealed class NetWorthSingleWriterTests
{
    /// <summary>
    /// Zweimal neu aufbauen legt den Tag nicht zweimal an.
    ///
    /// Das ist die Eigenschaft, an der es haengt: der Neuaufbau liest, was da ist, ergaenzt was fehlt
    /// und speichert. Wer sie hat, kann beliebig oft aufgerufen werden. Wer sie NICHT hat, braucht
    /// einen einzigen Schreiber - und genau den gab es nicht.
    /// </summary>
    [Fact]
    public async Task RebuildingTwiceDoesNotCreateTheDayTwice()
    {
        using var factory = new BackendWebApplicationFactory();
        var seed = await SeedAsync(factory);

        await factory.SeedAsync(async db =>
        {
            var service = new FinanzguruAccountReconciliationService(db, new AuditService(db));
            Assert.NotNull(await service.LinkExplicitAsync(
                seed.UserId, seed.SpaceId, seed.ImportAccountId, seed.TargetAccountId, 500m, "EUR", CancellationToken.None));
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await Rebuild(factory, seed);
        var first = await CountTodayAsync(factory, seed, today);
        await Rebuild(factory, seed);
        var second = await CountTodayAsync(factory, seed, today);

        Assert.NotEqual(0, first);
        Assert.Equal(first, second);
    }

    private static async Task Rebuild(BackendWebApplicationFactory factory, Seed seed)
    {
        using var scope = factory.Services.CreateScope();
        var snapshots = scope.ServiceProvider.GetRequiredService<FullWorth.Backend.Modules.Portfolio.NetWorthSnapshotService>();
        await snapshots.RebuildHistoryForUserAsync(seed.SpaceId, seed.UserId, null, CancellationToken.None);
    }

    private static async Task<int> CountTodayAsync(BackendWebApplicationFactory factory, Seed seed, DateOnly today)
    {
        var count = 0;
        await factory.SeedAsync(async db =>
            count = await db.NetWorthSnapshots.CountAsync(x => x.FullWorthSpaceId == seed.SpaceId && x.Date == today));
        return count;
    }

    private sealed record Seed(Guid UserId, Guid SpaceId, Guid ImportAccountId, Guid TargetAccountId);

    private static async Task<Seed> SeedAsync(BackendWebApplicationFactory factory)
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = seed.UserId,
                EmailNormalized = $"{seed.UserId:N}@LOCAL.TEST",
                DisplayName = "Test",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = seed.SpaceId, Name = "Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = seed.SpaceId,
                UserId = seed.UserId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.AddRange(
                new FinanceAccount
                {
                    Id = seed.ImportAccountId,
                    FullWorthSpaceId = seed.SpaceId,
                    Provider = FinanzguruAccountReconciliationService.ImportProvider,
                    IdentificationHash = "fg-history",
                    ProviderAccountId = "finanzguru:fg-history",
                    InstitutionName = "Finanzguru Import",
                    DisplayName = "Historie",
                    Currency = "EUR",
                    IsActive = false,
                    IncludeInNetWorth = false
                },
                new FinanceAccount
                {
                    Id = seed.TargetAccountId,
                    FullWorthSpaceId = seed.SpaceId,
                    Provider = "manual",
                    IdentificationHash = "target",
                    ProviderAccountId = "target",
                    InstitutionName = "Bank",
                    DisplayName = "Girokonto",
                    Currency = "EUR",
                    IsActive = true,
                    IncludeInNetWorth = true
                });
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = seed.ImportAccountId, UserId = seed.UserId, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = seed.TargetAccountId, UserId = seed.UserId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = Guid.NewGuid(),
                AccountId = seed.ImportAccountId,
                ExternalKey = "finanzguru:history-1",
                Status = "BOOK",
                BookingDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3),
                ValueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3),
                Amount = -25m,
                Currency = "EUR",
                UseForBalanceHistory = false,
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        });
        return seed;
    }
}
