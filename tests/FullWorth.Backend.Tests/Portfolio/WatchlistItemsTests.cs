using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Die Eintraege einer Merkliste.
///
/// Die Route hatte keinen Test. Sie prueft jeden Eintrag gegen den Bereich, und tat das bis
/// 2026-09-15 je Eintrag einzeln - und zwar blockierend (<c>GetAwaiter().GetResult()</c> mitten in
/// einem LINQ-Praedikat). Jetzt steht dahinter eine Abfrage mit <c>=ANY(@ids)</c>, die die Anzahl
/// vergleicht. Genau dieser Vergleich ist die Stelle, an der ein fremdes Wertpapier durchrutschen
/// koennte, darum steht hier beides: der gute Fall mit mehreren Eintraegen und der Fall, in dem
/// eines davon einem anderen Bereich gehoert.
/// </summary>
public sealed class WatchlistItemsTests
{
    [Fact]
    public async Task Items_are_stored_and_read_back()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        var ersteAktie = Guid.NewGuid();
        var zweiteAktie = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            await AnlegenAsync(db, userId);
            await WertpapierAsync(db, ersteAktie, FullWorthSpaceDefaults.LegacyId, "ERSTE_AKTIE");
            await WertpapierAsync(db, zweiteAktie, FullWorthSpaceDefaults.LegacyId, "ZWEITE_AKTIE");
        });

        var watchlistId = await MerklisteAnlegenAsync(client, userId);

        using var put = Request(HttpMethod.Put, $"/api/investments/watchlists/{watchlistId:D}/items", userId);
        put.Content = JsonContent.Create(new[]
        {
            new { securityId = ersteAktie, targetPrice = (decimal?)42.5m, notes = (string?)"ERSTE_NOTIZ" },
            new { securityId = zweiteAktie, targetPrice = (decimal?)null, notes = (string?)null }
        });
        using var putResponse = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        using var get = Request(HttpMethod.Get, $"/api/investments/watchlists/{watchlistId:D}/items", userId);
        using var getResponse = await client.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        using var body = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        var items = body.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item => item.GetProperty("name").GetString() == "ERSTE_AKTIE"
            && item.GetProperty("targetPrice").GetDecimal() == 42.5m);
        Assert.Contains(items, item => item.GetProperty("name").GetString() == "ZWEITE_AKTIE");
    }

    [Fact]
    public async Task A_security_of_another_space_is_rejected()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var userId = Guid.NewGuid();
        var fremderBereich = Guid.NewGuid();
        var eigeneAktie = Guid.NewGuid();
        var fremdeAktie = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            await AnlegenAsync(db, userId);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "FullWorthSpaces" ("Id","Name","BaseCurrency","CreatedAt","UpdatedAt")
VALUES ({fremderBereich},{"FREMDER_BEREICH"},{"EUR"},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow})
""");
            await WertpapierAsync(db, eigeneAktie, FullWorthSpaceDefaults.LegacyId, "EIGENE_AKTIE");
            await WertpapierAsync(db, fremdeAktie, fremderBereich, "FREMDE_AKTIE");
        });

        var watchlistId = await MerklisteAnlegenAsync(client, userId);

        // Ein gueltiges Wertpapier zusammen mit einem fremden: die ganze Liste wird abgelehnt.
        using var put = Request(HttpMethod.Put, $"/api/investments/watchlists/{watchlistId:D}/items", userId);
        put.Content = JsonContent.Create(new[]
        {
            new { securityId = eigeneAktie, targetPrice = (decimal?)null, notes = (string?)null },
            new { securityId = fremdeAktie, targetPrice = (decimal?)null, notes = (string?)null }
        });
        using var putResponse = await client.SendAsync(put);
        Assert.Equal(HttpStatusCode.BadRequest, putResponse.StatusCode);

        using var get = Request(HttpMethod.Get, $"/api/investments/watchlists/{watchlistId:D}/items", userId);
        using var getResponse = await client.SendAsync(get);
        using var body = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        Assert.Empty(body.RootElement.EnumerateArray());
    }

    private static async Task<Guid> MerklisteAnlegenAsync(HttpClient client, Guid userId)
    {
        using var request = Request(HttpMethod.Post, "/api/investments/watchlists", userId);
        request.Content = JsonContent.Create(new { name = "MEINE_MERKLISTE" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task AnlegenAsync(FullWorth.Backend.Data.FullWorthDbContext db, Guid userId)
    {
        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = $"{userId:N}@EXAMPLE.COM",
            DisplayName = "Beobachter",
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

    private static Task WertpapierAsync(
        FullWorth.Backend.Data.FullWorthDbContext db, Guid id, Guid space, string name) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities" ("Id","FullWorthSpaceId","Name","AssetType","Currency","CreatedAt","UpdatedAt")
VALUES ({id},{space},{name},{"stock"},{"EUR"},{DateTimeOffset.UtcNow},{DateTimeOffset.UtcNow})
""");

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var request = new HttpRequestMessage(
            method, $"{path}{separator}fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
