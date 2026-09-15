using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Die Kategorie-Ansicht.
///
/// Sie hatte keinen Test, obwohl in ihr die Regel steckt, welche Buchung auf einen Menschen wartet:
/// von Hand eingeordnet gilt als geprueft, aus einem fremden System uebernommen auch - nur was die
/// Regeln oder der Katalog geraten haben, ist offen. Dieser Test haelt beides fest, die Regel und die
/// Form der Antwort, an der die Oberflaeche haengt.
/// </summary>
public sealed class CategoryOverviewTests
{
    [Fact]
    public async Task Manual_counts_as_reviewed_and_a_guess_does_not()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var vonHand = Guid.NewGuid();
        var geraten = Guid.NewGuid();
        var uebernommen = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                DisplayName = "Einordner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            db.Categories.Add(new FinanceCategory
            {
                Id = categoryId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Key = $"test-{categoryId:N}",
                Name = "Lebensmittel"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = $"manual-{accountId:N}",
                ProviderAccountId = $"manual-{accountId:N}",
                InstitutionName = "Manual",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = userId,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.Transactions.AddRange(
                Buchung(vonHand, accountId, categoryId, "manual"),
                Buchung(geraten, accountId, categoryId, "catalog"),
                Buchung(uebernommen, accountId, categoryId, "provider"));
            await db.SaveChangesAsync();
        });

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/category-intelligence/overview?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("reviewed").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("needsReview").GetInt32());

        var items = body.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetGuid());
        Assert.True(items[vonHand].GetProperty("isReviewed").GetBoolean());
        Assert.True(items[uebernommen].GetProperty("isReviewed").GetBoolean());
        Assert.False(items[geraten].GetProperty("isReviewed").GetBoolean());

        // Die Form, an der die Oberflaeche haengt.
        var one = items[vonHand];
        Assert.Equal(categoryId, one.GetProperty("categoryId").GetGuid());
        Assert.Equal("manual", one.GetProperty("categorizationSource").GetString());
        Assert.Equal("manual", one.GetProperty("reasonCode").GetString());
        Assert.Equal(1.00m, one.GetProperty("confidence").GetDecimal());
        Assert.False(one.GetProperty("needsReview").GetBoolean());
        Assert.False(one.GetProperty("learningSuggested").GetBoolean());
        Assert.Equal(JsonValueKind.Null, one.GetProperty("categoryColor").ValueKind);
        Assert.Empty(one.GetProperty("tags").EnumerateArray());
    }

    private static FinanceTransaction Buchung(Guid id, Guid accountId, Guid categoryId, string source) => new()
    {
        Id = id,
        AccountId = accountId,
        ExternalKey = $"test-{id:N}",
        Status = "BOOK",
        BookingDate = new DateOnly(2026, 8, 20),
        ValueDate = new DateOnly(2026, 8, 20),
        Amount = -20m,
        Currency = "EUR",
        Counterparty = "REWE",
        NormalizedCounterparty = "REWE",
        CategoryId = categoryId,
        CategorizationSource = source,
        RawJson = "{}"
    };
}
