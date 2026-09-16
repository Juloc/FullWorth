using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Das Zuordnen laeuft ueber den ECHTEN Weg durch - ohne doppelten Schreiber der Vermoegenshistorie
/// (#123).
///
/// Im Protokoll stand:
///
/// <code>
/// ERROR: duplicate key value violates unique constraint
///        "IX_NetWorthSnapshots_FullWorthSpaceId_UserId_Date_Currency"
/// </code>
///
/// Zwei Handler in <c>FinanzguruImportEndpoints</c> riefen nach dem Verknuepfen
/// <c>RebuildHistoryForUserAsync</c> DIREKT auf. Das war doppelt - das Verknuepfen committet
/// vermoegenswirksame Daten, der SaveChanges-Interceptor hatte den
/// <c>FinancialDataConsistencyCoordinator</c> also laengst ueber den ganzen Space laufen lassen - und
/// es lief an dessen Semaphor vorbei. Zwei Neuaufbauten nebeneinander sehen beide "heute fehlt" und
/// legen beide an.
///
/// <para>
/// Dieser Test faehrt den Weg, den die Oberflaeche faehrt - Endpunkt statt Dienst - und haelt fest,
/// dass dabei genau eine Zeile je Tag und Waehrung entsteht. Den zweiten SCHREIBER faengt er
/// allerdings NICHT: der Fehler braucht echte Nebenlaeufigkeit. Sequenziell findet der zweite
/// Neuaufbau die schon committete Zeile und legt nichts an, der Test bleibt gruen. Genau das ist
/// passiert, als der Aufruf im Endpunkt wieder auftauchte.
///
/// Dafuer gibt es <c>Architecture.NetWorthSingleWriterGuardTests</c>: die Zusicherung ist strukturell
/// ("genau ein Aufrufer"), also wird sie strukturell geprueft.
/// </para>
/// </summary>
public sealed class FinanzguruLinkSingleWriterTests
{
    [Fact]
    public async Task LinkingOverHttpLeavesExactlyOneHistoryWriter()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var seed = await SeedAsync(factory);

        using var request = UserRequest(HttpMethod.Post,
            $"/api/import/finanzguru/accounts/{seed.ImportAccountId:D}/link?fullWorthSpaceId={seed.SpaceId:D}",
            seed.UserId);
        request.Content = JsonContent.Create(new
        {
            targetAccountId = seed.TargetAccountId,
            currentBalance = 500m,
            currentBalanceCurrency = "EUR"
        });

        using var response = await client.SendAsync(request);

        // Ein zweiter Schreiber schlaegt hier als 500 durch - der eindeutige Index auf
        // (Space, Benutzer, Datum, Waehrung) laesst den zweiten INSERT nicht zu.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var perDay = await db.NetWorthSnapshots
                .Where(row => row.FullWorthSpaceId == seed.SpaceId && row.Date == today && row.Currency == "EUR")
                .CountAsync();

            // Genau eine Zeile je Tag und Waehrung - der eindeutige Index sagt dasselbe, aber hier
            // steht es als Aussage und nicht als Nebenwirkung.
            Assert.Equal(1, perDay);

            var moved = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == seed.TransactionId);
            Assert.Equal(seed.TargetAccountId, moved.AccountId);
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Seed(Guid UserId, Guid SpaceId, Guid ImportAccountId, Guid TargetAccountId, Guid TransactionId);

    private static async Task<Seed> SeedAsync(BackendWebApplicationFactory factory)
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
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
                    IdentificationHash = "fg-link",
                    ProviderAccountId = "finanzguru:fg-link",
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
                Id = seed.TransactionId,
                AccountId = seed.ImportAccountId,
                ExternalKey = "finanzguru:link-1",
                Status = "BOOK",
                BookingDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2),
                ValueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2),
                Amount = -30m,
                Currency = "EUR",
                UseForBalanceHistory = false,
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
        });
        return seed;
    }
}
