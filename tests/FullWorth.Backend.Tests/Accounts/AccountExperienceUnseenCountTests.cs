using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// Wie viele Buchungen der Benutzer auf einem Konto noch nicht gesehen hat.
///
/// Diese Zahl entstand bis 2026-09-15 in einer Schleife: je Konto ein COUNT mit dem Gesehen-Zeitpunkt
/// dieses Kontos. Seit sie in einer Abfrage fuer alle Konten entsteht, haengt sie an einem JOIN auf
/// zwei entpackte Arrays - und ein Konto ohne Gesehen-Eintrag muss dabei weiterhin alles zaehlen.
/// Genau das prueft dieser Test, und zwar gegen die echte Datenbank: es ist rohes SQL, der Compiler
/// sagt dazu nichts.
/// </summary>
public sealed class AccountExperienceUnseenCountTests
{
    [Fact]
    public async Task Unseen_counts_are_per_account_and_count_everything_before_the_first_visit()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var user = Guid.NewGuid();
        var nieBesucht = Guid.NewGuid();
        var schonGesehen = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = user,
                EmailNormalized = $"{user:N}@EXAMPLE.COM",
                DisplayName = "Kontobesitzer",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = user,
                Role = FullWorthSpaceRoles.Owner
            });

            foreach (var (accountId, name) in new[] { (nieBesucht, "Nie besucht"), (schonGesehen, "Schon gesehen") })
            {
                db.Accounts.Add(new FinanceAccount
                {
                    Id = accountId,
                    FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                    Provider = "manual",
                    ProviderAccountId = $"manual-{accountId:N}",
                    IdentificationHash = $"manual-{accountId:N}",
                    InstitutionName = "Manual",
                    DisplayName = name,
                    Currency = "EUR",
                    IsActive = true
                });
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = accountId,
                    UserId = user,
                    OwnershipType = AccountOwnershipTypes.Owner
                });
            }

            // Drei auf dem einen, zwei auf dem anderen - damit eine vertauschte Zuordnung auffaellt.
            for (var index = 0; index < 3; index++) db.Transactions.Add(Buchung(nieBesucht, index));
            for (var index = 0; index < 2; index++) db.Transactions.Add(Buchung(schonGesehen, index));
            await db.SaveChangesAsync();
        });

        // Ein Konto wird besucht, das andere nicht.
        using var seen = Request(HttpMethod.Post,
            $"/api/account-experience/{schonGesehen:D}/seen?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var seenResponse = await client.SendAsync(seen);
        seenResponse.EnsureSuccessStatusCode();

        using var list = Request(HttpMethod.Get,
            $"/api/account-experience/?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}", user);
        using var response = await client.SendAsync(list);
        response.EnsureSuccessStatusCode();

        var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
        var proKonto = rows.EnumerateArray().ToDictionary(
            row => row.GetProperty("accountId").GetGuid(),
            row => row.GetProperty("unseenTransactions").GetInt32());

        Assert.Equal(3, proKonto[nieBesucht]);
        Assert.Equal(0, proKonto[schonGesehen]);
    }

    private static FinanceTransaction Buchung(Guid accountId, int index) => new()
    {
        AccountId = accountId,
        ExternalKey = $"{accountId:N}-{index}",
        Amount = -10m - index,
        Currency = "EUR",
        BookingDate = new DateOnly(2026, 3, 1).AddDays(index),
        FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-10 + index)
    };

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
