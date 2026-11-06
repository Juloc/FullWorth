using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// Die Buchungsliste sortiert nach Zahltag. Vorgemerkt war lange ein Sortierkriterium vor dem Datum: jede
/// vorgemerkte Zeile stand vor jeder gebuchten, die Liste begann mit einem Block, der nicht dorthin
/// gehoert, und "Heute" stand darunter - falschherum. Die Bank liefert den Zahltag einer vorgemerkten
/// Karte schon als Buchungsdatum, also steht sie an diesem Tag und nur das Kennzeichen unterscheidet sie.
/// </summary>
public sealed class TransactionPendingOrderTests
{
    [Fact]
    public async Task Pending_entries_are_listed_at_their_pay_date_between_booked_ones()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var rows = await RowsAsync(client, scenario);

        // Der Zahltag entscheidet, nicht der Status und nicht die Valuta: "pending-last-week" hat eine Valuta in der
        // Zukunft und steht trotzdem ganz unten.
        Assert.Equal(
            ["booked-today", "pending-today", "booked-yesterday", "pending-undated", "pending-last-week"],
            rows.Select(row => row.Key).ToArray());
    }

    // Eine vorgemerkte Zeile ohne Buchungsdatum wird nach der Valuta eingeordnet - dort, wo sie bezahlt
    // wurde, nicht ganz oben.
    [Fact]
    public async Task A_pending_entry_without_a_booking_date_is_placed_by_its_value_date()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var rows = await RowsAsync(client, scenario);

        Assert.True(
            rows.FindIndex(row => row.Key == "pending-today") < rows.FindIndex(row => row.Key == "pending-undated"),
            $"got {string.Join(", ", rows.Select(row => row.Key))}");
    }

    private sealed record Scenario(Guid Owner, Guid Space);

    private sealed record Row(string Key, string Status);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();
        var account = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Order owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Order", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = account,
                FullWorthSpaceId = space,
                Provider = "test",
                IdentificationHash = $"order-{account:N}",
                ProviderAccountId = $"order-{account:N}",
                InstitutionName = "Bank",
                DisplayName = "Giro",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = account,
                UserId = owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });

            // Die Form, die die Bank liefert: eine vorgemerkte Karte hat ihren Zahltag als Buchungsdatum und
            // einen Platzhalter als Valuta (hier +9 Tage) - der darf die Reihenfolge nicht bestimmen.
            // "pending-undated" hat nur die Valuta. Am selben Tag entscheidet der Beobachtungszeitpunkt:
            // die Buchung heute wurde zuletzt gesehen, steht aber trotzdem ueber der vorgemerkten.
            var seenAt = DateTimeOffset.UtcNow;
            foreach (var (key, status, booking, value, updated) in new[]
                     {
                         ("pending-last-week", "PDNG", (DateOnly?)today.AddDays(-7), (DateOnly?)today.AddDays(2), seenAt.AddMinutes(-5)),
                         ("booked-yesterday", "BOOK", (DateOnly?)today.AddDays(-1), (DateOnly?)today.AddDays(-1), seenAt.AddMinutes(-1)),
                         ("pending-undated", "PDNG", null, today.AddDays(-1), seenAt.AddMinutes(-3)),
                         ("pending-today", "PDNG", today, today.AddDays(9), seenAt.AddMinutes(-4)),
                         ("booked-today", "BOOK", today, today, seenAt.AddMinutes(-2))
                     })
                db.Transactions.Add(new FinanceTransaction
                {
                    AccountId = account,
                    ExternalKey = key,
                    Description = key,
                    Status = status,
                    BookingDate = booking,
                    ValueDate = value,
                    Amount = -10m,
                    Currency = "EUR",
                    UpdatedAt = updated
                });
            await db.SaveChangesAsync();
        });

        return new Scenario(owner, space);
    }

    private static async Task<List<Row>> RowsAsync(HttpClient client, Scenario scenario)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/transactions?fullWorthSpaceId={scenario.Space}&limit=50");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Select(row => new Row(
                row.GetProperty("description").GetString() ?? string.Empty,
                row.GetProperty("status").GetString() ?? string.Empty))
            .ToList();
    }
}
