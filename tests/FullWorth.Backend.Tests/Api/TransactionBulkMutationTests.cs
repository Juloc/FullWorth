using FullWorth.Backend.Data;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Was die Massenänderung an einer Buchung ändert — und warum es davon nur noch eine gibt (#177).
///
/// Es waren zwei, beide fertig gebaut und beide ohne Aufrufer:
///
///   <c>/preview</c> + <c>/execute</c>          TransactionBulkStore, 264 Zeilen
///   <c>/advanced-preview</c> + <c>/apply</c>   AdvancedBulkStore, 452 Zeilen
///
/// Bevor eine Oberfläche dazukam, musste feststehen, welche bleibt — sonst zementiert die Oberfläche
/// die Doppelung, und dieses Haus trägt ohnehin an mehr als einer Stelle ein altes und ein neues
/// System nebeneinander. Geblieben ist die zweite: sie kann alles, was die erste konnte, und darüber
/// hinaus Stichworte, Überweisungspaare und die Sicherung über ExpectedCount.
///
/// Vor dem Löschen hat dieser Test in seiner ersten Fassung geprüft, dass beide im gemeinsamen Teil
/// dasselbe tun. Sie taten es — bis auf einen Fall, und der hat die Wahl entschieden: bei leerem
/// Text schrieb die alte Maschine den leeren String, die neue schreibt NULL. „Keine Notiz" ist NULL;
/// ein leerer String ist eine Notiz, die zufällig nichts enthält, und er taucht in jeder Abfrage auf,
/// die nach „hat eine Notiz" sucht.
///
/// Was hier steht, ist deshalb keine Doppelprüfung mehr, sondern die Zusicherung selbst.
/// </summary>
public sealed class TransactionBulkMutationTests
{
    [Fact]
    public async Task Category_ignored_reviewed_and_note_are_all_written()
    {
        var ergebnis = await RunAsync();

        Assert.Equal(new Guid("11111111-2222-3333-4444-555555555555"), ergebnis.CategoryId);
        Assert.True(ergebnis.IsIgnored);
        Assert.True(ergebnis.IsReviewed);
        Assert.Equal("Sammeländerung", ergebnis.Note);
        // Von Hand gesetzt heißt: keine spätere Automatik überschreibt das wieder.
        Assert.Equal("manual", ergebnis.CategorizationSource);
    }

    /// <summary>
    /// Ohne Text ist die Notiz leer und nicht „ein leerer Text" — der Unterschied, an dem sich die
    /// Wahl zwischen den beiden Maschinen entschieden hat.
    /// </summary>
    [Fact]
    public async Task An_empty_note_becomes_null_and_not_an_empty_string()
    {
        var ergebnis = await RunAsync(note: "   ");

        Assert.Null(ergebnis.Note);
    }

    private sealed record Ergebnis(Guid? CategoryId, bool IsIgnored, bool IsReviewed, string? Note, string? CategorizationSource);

    private static async Task<Ergebnis> RunAsync(string note = "Sammeländerung")
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        // Fest statt zufaellig: die beiden Laeufe haben je eine eigene Datenbank, und verglichen wird
        // ueber beide hinweg - mit zwei zufaelligen Kennungen vergleicht man nur den Zufall.
        var categoryId = new Guid("11111111-2222-3333-4444-555555555555");
        var buchung = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Bulk owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                ProviderAccountId = $"manual-{accountId:N}",
                IdentificationHash = $"manual-{accountId:N}",
                InstitutionName = "Manual",
                DisplayName = "Bulk account",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = "bulk-target",
                Name = "Sammelziel"
            });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = buchung,
                AccountId = accountId,
                ExternalKey = $"{buchung:N}",
                Status = "BOOK",
                BookingDate = new DateOnly(2026, 3, 1),
                Amount = -40m,
                Currency = "EUR",
                Counterparty = "Sammelkandidat"
            });
            await db.SaveChangesAsync();
        });

        using var request = Request(
            $"/api/transaction-bulk/apply?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        request.Content = JsonContent.Create(new
        {
            transactionIds = new[] { buchung },
            expectedCount = 1,
            confirmSelection = true,
            updateCategory = true,
            categoryId,
            isIgnored = true,
            isReviewed = true,
            replaceNotes = true,
            note,
            confirmReplaceNotes = true
        });

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var row = await db.Transactions.AsNoTracking().SingleAsync(item => item.Id == buchung);
        // Der Pruefstatus liegt in einer eigenen Tabelle ohne Entitaet - gelesen wie im
        // Nachbartest, per roher Abfrage.
        var reviewed = await db.Database
            .SqlQuery<Guid>($"SELECT \"TransactionId\" AS \"Value\" FROM \"TransactionReviewStates\" WHERE \"IsReviewed\"")
            .ToListAsync();
        return new Ergebnis(row.CategoryId, row.IsIgnored, reviewed.Contains(buchung), row.UserNote, row.CategorizationSource);
    }

    private static HttpRequestMessage Request(string url, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
