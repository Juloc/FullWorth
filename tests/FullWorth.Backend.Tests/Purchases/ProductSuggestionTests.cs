using System.Net;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Der Produktvorschlag zu einem Text von einem Kassenbon.
///
/// Die Route hatte keinen Test. Sie verglich den Namen im Arbeitsspeicher und holte dafuer die ersten
/// 2000 Produkte des Bereichs - wer mehr hatte, bekam ab dem 2001. Produkt nie einen Treffer, ohne
/// dass irgendwo eine Grenze stand. Der Vergleich steht jetzt in SQL, und der zweite Test hier legt
/// genau deshalb 2500 Produkte an: er ist rot, sobald jemand die Obergrenze wieder einbaut.
/// </summary>
public sealed class ProductSuggestionTests
{
    [Fact]
    public async Task Punctuation_and_case_do_not_matter()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            await MitgliedAsync(db, userId);
            await ProduktAsync(db, Guid.NewGuid(), "Bio-Vollmilch 3,5%");
        });

        var match = await VorschlagAsync(client, userId, "biovollmilch35");
        Assert.Equal("Bio-Vollmilch 3,5%", match.GetProperty("canonicalName").GetString());
        Assert.Equal("canonical_name", match.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_product_beyond_the_first_two_thousand_is_still_found()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            await MitgliedAsync(db, userId);
            // 2500 Produkte, deren Namen alle hinter dem gesuchten liegen - egal in welcher
            // Reihenfolge gelesen wird, das gesuchte ist nicht unter den ersten 2000.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Products" ("Id","FullWorthSpaceId","CanonicalName","DefaultQuantityUnit","IsArchived","CreatedAt","UpdatedAt")
SELECT gen_random_uuid(),{FullWorthSpaceDefaults.LegacyId},'Fuellprodukt '||i,'piece',false,{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow}
FROM generate_series(1,2500) AS i
""");
            await ProduktAsync(db, Guid.NewGuid(), "Zimtsterne");
        });

        var match = await VorschlagAsync(client, userId, "Zimtsterne");
        Assert.Equal("Zimtsterne", match.GetProperty("canonicalName").GetString());
    }

    [Fact]
    public async Task A_stored_spelling_wins_over_the_product_name()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        var produktId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            await MitgliedAsync(db, userId);
            await ProduktAsync(db, produktId, "Haferdrink Barista");
            await ProduktAsync(db, Guid.NewGuid(), "HAFERDRINK");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "ProductAliases" ("Id","ProductId","Alias","NormalizedAlias","AliasType","CreatedAt")
VALUES ({Guid.NewGuid()},{produktId},{"HAFERDRINK"},{"haferdrink"},{"manual"},{DateTimeOffset.UtcNow})
""");
        });

        var match = await VorschlagAsync(client, userId, "Haferdrink");
        Assert.Equal("Haferdrink Barista", match.GetProperty("canonicalName").GetString());
        Assert.Equal("manual", match.GetProperty("source").GetString());
    }

    private static async Task<JsonElement> VorschlagAsync(HttpClient client, Guid userId, string text)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/product-identities/suggest?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&text={Uri.EscapeDataString(text)}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
        return body.RootElement.Clone();
    }

    private static async Task MitgliedAsync(FullWorthDbContext db, Guid userId)
    {
        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM",
            DisplayName = "Einkaeufer",
            IsActive = true
        });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
        {
            FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
            UserId = userId,
            Role = FullWorthSpaceRoles.Owner
        });
        await db.SaveChangesAsync();
    }

    private static Task ProduktAsync(FullWorthDbContext db, Guid id, string name) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Products" ("Id","FullWorthSpaceId","CanonicalName","DefaultQuantityUnit","IsArchived","CreatedAt","UpdatedAt")
VALUES ({id},{FullWorthSpaceDefaults.LegacyId},{name},{"piece"},{false},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow})
""");
}
