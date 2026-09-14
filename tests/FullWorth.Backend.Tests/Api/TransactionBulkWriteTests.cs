using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Die beiden Schreibvorgaenge von <c>/api/transaction-bulk/execute</c>, die nicht ueber EF laufen:
/// der Pruefstatus und die Vertragsverknuepfung.
///
/// Beide waren bis 2026-09-15 eine Schleife mit je einer Anweisung pro Buchung - bei zehntausend
/// Treffern zehntausend Runden zur Datenbank. Jetzt ist es je eine Anweisung ueber ein entpacktes
/// Array, und das ist rohes SQL: der Compiler sagt dazu nichts, und kein Test hat diese Route
/// vorher beruehrt.
///
/// Der Vertrag bekommt nur Ausgaben. Eine Gutschrift dort zu verknuepfen wuerde den Vertrag guenstiger
/// aussehen lassen, als er ist.
/// </summary>
public sealed class TransactionBulkWriteTests
{
    [Fact]
    public async Task Review_state_and_contract_links_are_written_for_every_match()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var ausgabeA = Guid.NewGuid();
        var ausgabeB = Guid.NewGuid();
        var gutschrift = Guid.NewGuid();

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
            db.Contracts.Add(new RecurringContract
            {
                Id = contractId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Name = "Strom",
                Amount = 40m,
                Currency = "EUR",
                BillingCycle = "monthly",
                Interval = 1,
                IsActive = true
            });

            db.Transactions.Add(Buchung(ausgabeA, accountId, -40m));
            db.Transactions.Add(Buchung(ausgabeB, accountId, -41m));
            db.Transactions.Add(Buchung(gutschrift, accountId, 12m));
            await db.SaveChangesAsync();
        });

        using var request = Request($"/api/transaction-bulk/execute?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", userId);
        request.Content = JsonContent.Create(new
        {
            filter = new { },
            isReviewed = true,
            contractId
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var reviewed = await db.Database
                .SqlQuery<Guid>($"SELECT \"TransactionId\" AS \"Value\" FROM \"TransactionReviewStates\" WHERE \"IsReviewed\"")
                .ToListAsync();
            // Alle drei Treffer, nicht nur der erste der Schleife.
            Assert.Equal(3, reviewed.Count);
            Assert.Contains(gutschrift, reviewed);

            var verknuepft = await db.Database
                .SqlQuery<Guid>($"SELECT \"TransactionId\" AS \"Value\" FROM \"ContractTransactionLinks\" WHERE \"ContractId\"={contractId}")
                .ToListAsync();
            Assert.Equal(2, verknuepft.Count);
            Assert.Contains(ausgabeA, verknuepft);
            Assert.Contains(ausgabeB, verknuepft);
            Assert.DoesNotContain(gutschrift, verknuepft);
        });
    }

    private static FinanceTransaction Buchung(Guid id, Guid accountId, decimal amount) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = $"{id:N}",
        Status = "BOOK",
        BookingDate = new DateOnly(2026, 3, 1),
        Amount = amount,
        Currency = "EUR",
        Counterparty = "Stadtwerke"
    };

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
