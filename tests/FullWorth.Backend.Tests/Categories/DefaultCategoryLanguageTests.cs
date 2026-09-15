using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Categories;

/// <summary>
/// Die Standardkategorien gab es nur auf Englisch, und der Einrichtungsassistent fragte nicht (#117).
///
/// Der zweite Teil des Fehlers war schlimmer als der erste: der Seeder lief bei JEDEM Start fuer
/// JEDEN Space und legte jeden Standardschluessel an, der gerade fehlte. Wer eine Standardkategorie
/// loeschte, hatte sie nach dem naechsten Neustart wieder - auf Englisch, neben den deutschen, die
/// ein Import angelegt hatte. Das ist das gemeldete "gemischte Set".
/// </summary>
public sealed class DefaultCategoryLanguageTests
{
    [Fact]
    public async Task A_german_space_gets_german_default_categories()
    {
        using var factory = new BackendWebApplicationFactory();
        var space = await SeedSpaceAsync(factory, DefaultCategoryNames.German);

        await factory.SeedAsync(async db =>
        {
            var names = await db.Categories.AsNoTracking()
                .Where(category => category.FullWorthSpaceId == space.SpaceId)
                .ToDictionaryAsync(category => category.Key, category => category.Name);

            Assert.Equal("Lebensmittel", names["food.groceries"]);
            Assert.Equal("Wohnen", names["housing"]);
            Assert.Equal("Umbuchungen", names["transfers"]);
            // Und NUR das deutsche Set: kein englischer Zwilling daneben.
            Assert.DoesNotContain("Groceries", names.Values);
            Assert.Equal(FullWorthSeeder.DefaultCategoryCount, names.Count);
        });
    }

    [Fact]
    public async Task An_english_space_gets_english_default_categories()
    {
        using var factory = new BackendWebApplicationFactory();
        var space = await SeedSpaceAsync(factory, DefaultCategoryNames.English);

        await factory.SeedAsync(async db =>
        {
            var names = await db.Categories.AsNoTracking()
                .Where(category => category.FullWorthSpaceId == space.SpaceId)
                .ToDictionaryAsync(category => category.Key, category => category.Name);

            Assert.Equal("Groceries", names["food.groceries"]);
            Assert.DoesNotContain("Lebensmittel", names.Values);
            Assert.Equal(FullWorthSeeder.DefaultCategoryCount, names.Count);
        });
    }

    /// <summary>
    /// Der eigentliche Fehler: ein zweiter Durchlauf des Seeders - im Betrieb ist das der naechste
    /// Start - darf nichts nachlegen. Vorher wuchs eine geloeschte Standardkategorie wieder nach.
    /// </summary>
    [Fact]
    public async Task A_second_start_adds_nothing_even_after_a_default_category_was_deleted()
    {
        using var factory = new BackendWebApplicationFactory();
        var space = await SeedSpaceAsync(factory, DefaultCategoryNames.German);

        await factory.SeedAsync(async db =>
        {
            var doomed = await db.Categories
                .SingleAsync(category => category.FullWorthSpaceId == space.SpaceId && category.Key == "pets.vet");
            db.Categories.Remove(doomed);
            await db.SaveChangesAsync();
        });

        await factory.SeedAsync(async db =>
            await new FullWorthSeeder().SeedAsync(db, CancellationToken.None));

        await factory.SeedAsync(async db =>
        {
            var keys = await db.Categories.AsNoTracking()
                .Where(category => category.FullWorthSpaceId == space.SpaceId)
                .Select(category => category.Key)
                .ToListAsync();

            Assert.DoesNotContain("pets.vet", keys);
            Assert.Equal(FullWorthSeeder.DefaultCategoryCount - 1, keys.Count);
        });
    }

    [Fact]
    public async Task The_wizard_can_switch_the_language_while_nobody_has_touched_the_defaults()
    {
        using var factory = new BackendWebApplicationFactory();
        var space = await SeedSpaceAsync(factory, DefaultCategoryNames.English);
        using var client = factory.CreateClient();

        using var before = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/categories/language?fullWorthSpaceId={space.SpaceId:D}", space.OwnerId));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var beforeJson = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        Assert.Equal("en", beforeJson.RootElement.GetProperty("language").GetString());
        Assert.True(beforeJson.RootElement.GetProperty("canChange").GetBoolean());

        using var switched = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/categories/language?fullWorthSpaceId={space.SpaceId:D}", space.OwnerId,
            new { language = "de" }));
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var names = await db.Categories.AsNoTracking()
                .Where(category => category.FullWorthSpaceId == space.SpaceId)
                .ToDictionaryAsync(category => category.Key, category => category.Name);
            Assert.Equal("Lebensmittel", names["food.groceries"]);
            // Umbenannt, nicht nachgelegt.
            Assert.Equal(FullWorthSeeder.DefaultCategoryCount, names.Count);
        });
    }

    /// <summary>Eine Umbenennung ist eine Entscheidung. Eine Spracheinstellung ueberschreibt sie nicht.</summary>
    [Fact]
    public async Task A_renamed_default_category_locks_the_language_switch()
    {
        using var factory = new BackendWebApplicationFactory();
        var space = await SeedSpaceAsync(factory, DefaultCategoryNames.English);
        await factory.SeedAsync(async db =>
        {
            var renamed = await db.Categories
                .SingleAsync(category => category.FullWorthSpaceId == space.SpaceId && category.Key == "food.groceries");
            renamed.Name = "Supermarkt";
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();
        using var state = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/categories/language?fullWorthSpaceId={space.SpaceId:D}", space.OwnerId));
        using var json = JsonDocument.Parse(await state.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("canChange").GetBoolean());

        using var refused = await client.SendAsync(Request(HttpMethod.Put,
            $"/api/categories/language?fullWorthSpaceId={space.SpaceId:D}", space.OwnerId,
            new { language = "de" }));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var name = await db.Categories.AsNoTracking()
                .Where(category => category.FullWorthSpaceId == space.SpaceId && category.Key == "food.groceries")
                .Select(category => category.Name)
                .SingleAsync();
            Assert.Equal("Supermarkt", name);
        });
    }

    /// <summary>Jeder Standardschluessel hat eine deutsche Fassung - sonst entstuende genau das gemischte Set.</summary>
    [Fact]
    public void Every_default_key_has_a_german_name()
    {
        var missing = FullWorthSeeder.DefaultCategoryKeys
            .Where(key => !DefaultCategoryNames.TranslatedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            "Diesen Standardkategorien fehlt der deutsche Name: " + string.Join(", ", missing));
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(Guid SpaceId, Guid OwnerId)> SeedSpaceAsync(
        BackendWebApplicationFactory factory, string language)
    {
        var spaceId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = ownerId,
                EmailNormalized = $"{ownerId:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = spaceId,
                Name = "Sprache",
                BaseCurrency = "EUR",
                DefaultCategoryLanguage = language
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = ownerId,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
            await new FullWorthSeeder().SeedDefaultCategoriesForSpaceAsync(db, spaceId, CancellationToken.None, language);
        });
        return (spaceId, ownerId);
    }
}
