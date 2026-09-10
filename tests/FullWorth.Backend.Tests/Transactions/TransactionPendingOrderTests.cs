using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// The booking list groups by booking date and labels today's group "Heute". Ordering by booking date
/// alone put a pending entry dated today *underneath* that header, mixed in with real bookings — so the
/// header no longer marked the boundary between what is booked and what is not. A pending entry with no
/// booking date at all landed above the header instead, in an unlabelled group, because PostgreSQL sorts
/// NULLs first on a descending order.
/// </summary>
public sealed class TransactionPendingOrderTests
{
    [Fact]
    public async Task Pending_entries_are_listed_ahead_of_every_booked_entry()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var rows = await RowsAsync(client, scenario);

        var lastPending = rows.FindLastIndex(row => row.Status == "PDNG");
        var firstBooked = rows.FindIndex(row => row.Status == "BOOK");
        Assert.True(lastPending >= 0 && firstBooked >= 0, "the scenario has both kinds");
        Assert.True(
            lastPending < firstBooked,
            $"pending must precede every booking, got {string.Join(", ", rows.Select(row => $"{row.Key}/{row.Status}"))}");
    }

    // Within the pending block the value date stands in for a booking date the bank has not published
    // yet - that entry belongs at its real position, not at the very top of the whole list.
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

    [Fact]
    public async Task Booked_entries_keep_their_newest_first_order()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var rows = await RowsAsync(client, scenario);

        Assert.Equal(
            ["booked-today", "booked-yesterday"],
            rows.Where(row => row.Status == "BOOK").Select(row => row.Key).ToArray());
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

            // "pending-undated" is the shape Enable Banking delivers for a card authorisation: a status
            // and a value date, no booking date yet. The observation timestamps are the tie-breaker the
            // old ordering fell back to inside a booking day, so the booked entry is the one seen last -
            // exactly the case where a pending row dated today ended up below it.
            var seenAt = DateTimeOffset.UtcNow;
            foreach (var (key, status, booking, value, updated) in new[]
                     {
                         ("booked-yesterday", "BOOK", (DateOnly?)today.AddDays(-1), (DateOnly?)today.AddDays(-1), seenAt.AddMinutes(-4)),
                         ("pending-undated", "PDNG", null, today.AddDays(-1), seenAt.AddMinutes(-3)),
                         ("pending-today", "PDNG", today, today, seenAt.AddMinutes(-2)),
                         ("booked-today", "BOOK", today, today, seenAt.AddMinutes(-1))
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
